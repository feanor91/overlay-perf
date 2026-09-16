import json

import pytest

from overlay.cli import build_parser, main
from overlay.config import Config
from overlay.runtime import build_runtime


@pytest.fixture
def config_simulee(tmp_path):
    chemin = tmp_path / "config.toml"
    chemin.write_text(
        """
        [general]
        mock = true
        poll_interval = 0.05
        [server]
        port = 8899
        token = "jeton-cli"
        """,
        encoding="utf-8",
    )
    return chemin


def test_analyse_des_arguments():
    args = build_parser().parse_args(["-vv", "serve", "--port", "9000"])
    assert args.command == "serve" and args.port == 9000 and args.verbose == 2


def test_aucune_sous_commande_equivaut_a_run(monkeypatch, config_simulee):
    """« overlay » seul doit lancer overlay + serveur, sans avoir a taper « run »."""
    from overlay import cli as cli_mod

    appels: list = []
    monkeypatch.setattr(
        cli_mod,
        "commande_run",
        lambda config, *, overlay, server, args: appels.append((overlay, server)) or 0,
    )
    assert main(["--config", str(config_simulee)]) == 0
    assert appels == [(True, True)]


def test_commande_sensors(config_simulee, capsys):
    assert main(["--config", str(config_simulee), "sensors"]) == 0
    sortie = capsys.readouterr().out
    assert "Backends :" in sortie
    assert "cpu.load" in sortie
    assert "fps.current" in sortie


def test_commande_sensors_en_json(config_simulee, capsys):
    assert main(["--config", str(config_simulee), "sensors", "--json"]) == 0
    corps = json.loads(capsys.readouterr().out)
    assert {b["name"] for b in corps["backends"]} == {"fps", "mock"}
    assert any(r["key"] == "gpu.0.temp" for r in corps["readings"])


def test_commande_pair(config_simulee, capsys):
    assert main(["--config", str(config_simulee), "pair"]) == 0
    sortie = capsys.readouterr().out
    assert ":8899/#token=jeton-cli" in sortie
    assert "Jeton : jeton-cli" in sortie


def test_commande_config_path(tmp_path, capsys):
    cible = tmp_path / "absent.toml"
    assert main(["--config", str(cible), "config", "--path"]) == 0
    assert str(cible) in capsys.readouterr().out


def test_commande_config_init(tmp_path, capsys):
    cible = tmp_path / "nouveau.toml"
    assert main(["--config", str(cible), "config", "--init"]) == 0
    assert cible.is_file()
    assert "[overlay]" in cible.read_text(encoding="utf-8")
    # Un fichier existant n'est jamais ecrase.
    assert main(["--config", str(cible), "config", "--init"]) == 1


def test_configuration_invalide_arrete_la_commande(tmp_path):
    mauvaise = tmp_path / "mauvaise.toml"
    mauvaise.write_text('[overlay]\nposition = "nulle part"', encoding="utf-8")
    with pytest.raises(SystemExit) as sortie:
        main(["--config", str(mauvaise), "sensors"])
    assert sortie.value.code == 2


def test_rien_a_lancer(config_simulee, capsys):
    assert main(["--config", str(config_simulee), "run", "--no-overlay", "--no-server"]) == 1
    assert "Rien a lancer" in capsys.readouterr().err


def test_assemblage_du_runtime():
    config = Config()
    config.general.mock = True
    runtime = build_runtime(config, need_token=False)
    # Le FPS est place en tete pour apparaitre en premier dans l'overlay.
    assert [b.name for b in runtime.hub.backends] == ["fps", "mock"]
    assert runtime.tracker is not None
    assert runtime.hub.poll_sync().readings[0].key == "fps.current"


def test_runtime_sans_suivi_fps():
    config = Config()
    config.general.mock = True
    config.fps.mode = "off"
    runtime = build_runtime(config, need_token=False)
    assert runtime.tracker is None
    assert [b.name for b in runtime.hub.backends] == ["mock"]


def test_source_de_trames_absente_ne_bloque_pas():
    config = Config()
    config.general.mock = True
    config.fps.mode = "push"
    runtime = build_runtime(config, need_token=False)
    assert runtime.frame_source is None
    runtime.start_frame_source()  # ne doit pas lever
    runtime.stop_frame_source()


def test_runtime_sans_avertissement_par_defaut():
    config = Config()
    config.general.mock = True
    # Isole des avertissements FPS : "mock" ne dispense pas de la detection de
    # source de trames, qui a son propre test plus bas.
    config.fps.mode = "off"
    assert build_runtime(config, need_token=False).warnings == []


def test_runtime_porte_les_avertissements_de_detection(monkeypatch):
    """Le mode simule court-circuite la detection : on verifie le relais lui-meme."""
    from overlay import runtime as runtime_mod

    def detection_simulee(**kwargs):
        avertissements = kwargs.get("warnings")
        if avertissements is not None:
            avertissements.append("message de detection")
        from overlay.sensors.mock import MockBackend

        return [MockBackend()]

    monkeypatch.setattr(runtime_mod, "detect_backends", detection_simulee)
    config = Config()
    config.fps.mode = "off"
    runtime = build_runtime(config, need_token=False)
    assert runtime.warnings == ["message de detection"]


def test_runtime_porte_les_avertissements_de_source_fps(monkeypatch):
    """Meme relais que pour la detection de capteurs, cote source de trames.

    C'est le trajet complet reellement emprunte par la CLI : avant ce correctif,
    ce cas (mode "auto", rien trouve) ne produisait absolument rien, meme en mode
    verbeux, laissant FPS et 1 % low manquer sans le moindre indice.
    """
    monkeypatch.setattr("shutil.which", lambda _: None)
    config = Config()
    config.general.mock = True
    runtime = build_runtime(config, need_token=False)
    assert len(runtime.warnings) == 1
    assert "Aucune source de FPS trouvee" in runtime.warnings[0]


def test_avertissements_affiches_clairement_sur_la_sortie_d_erreur(capsys):
    from overlay.cli import _imprimer_avertissements

    _imprimer_avertissements(["premier probleme", "second probleme"])
    sortie = capsys.readouterr()
    assert sortie.out == ""  # jamais sur stdout : ne doit pas polluer --json
    assert "Attention : premier probleme" in sortie.err
    assert "Attention : second probleme" in sortie.err


def test_commande_run_affiche_les_avertissements_de_detection(monkeypatch, capsys, tmp_path):
    """Reproduit un demarrage sous Windows sans LibreHardwareMonitor joignable."""
    from overlay import cli as cli_mod

    def detection_simulee(**kwargs):
        avertissements = kwargs.get("warnings")
        if avertissements is not None:
            avertissements.append("LibreHardwareMonitor injoignable sur test")
        from overlay.sensors.mock import MockBackend

        return [MockBackend()]

    monkeypatch.setattr("overlay.runtime.detect_backends", detection_simulee)
    # Le jeton serait sinon ecrit dans le vrai repertoire de donnees de l'utilisateur.
    monkeypatch.setattr("overlay.config.data_path", lambda: tmp_path)
    # Isole la commande de l'execution reseau reelle (uvicorn.serve bloquerait) :
    # seul l'affichage des avertissements, avant tout demarrage, nous interesse ici.
    monkeypatch.setattr(cli_mod, "_run_serveur_seul", lambda runtime: 0)

    config = Config()
    config.fps.mode = "off"

    args = cli_mod.build_parser().parse_args(["serve"])
    code = cli_mod.commande_run(config, overlay=False, server=True, args=args)

    assert code == 0
    assert "Attention : LibreHardwareMonitor injoignable sur test" in capsys.readouterr().err


# --- Surveillance de la source FPS apres demarrage --------------------------
#
# Un executable trouve et lance avec succes (le processus Python demarre sans
# lever) peut ne jamais transmettre une seule trame utile — par exemple si le
# nom recherche correspond en realite a un tout autre programme partageant le
# meme nom de fichier (rencontre en pratique : « PresentMon.exe » designe a la
# fois l'outil console attendu et l'application graphique PresentMon Capture).
# Rien dans le cycle de vie du processus ne le signale de lui-meme.


class _SourceFactice:
    name = "presentmon"


def _runtime_factice(tracker):
    from overlay.hub import MetricsHub
    from overlay.runtime import Runtime
    from overlay.sensors.mock import MockBackend

    return Runtime(
        config=Config(),
        hub=MetricsHub([MockBackend()], poll_interval=1.0),
        tracker=tracker,
        frame_source=_SourceFactice(),
        token="jeton",
    )


async def test_surveillance_avertit_si_aucune_trame_recue(capsys):
    from overlay.cli import _surveiller_source_fps
    from overlay.fps.tracker import FrameTimeTracker

    runtime = _runtime_factice(FrameTimeTracker(window_seconds=5.0))
    await _surveiller_source_fps(runtime, delai=0.02)

    erreur = capsys.readouterr().err
    assert "Attention" in erreur
    assert "presentmon" in erreur
    # La cause la plus probable (rencontree en pratique) doit etre nommee, pas
    # seulement "quelque chose ne va pas".
    assert "PresentMon Capture" in erreur
    assert "presentmon_path" in erreur
    assert "administrateur" in erreur


async def test_surveillance_silencieuse_si_des_trames_arrivent(capsys):
    from overlay.cli import _surveiller_source_fps
    from overlay.fps.tracker import FrameTimeTracker

    tracker = FrameTimeTracker(window_seconds=5.0)
    tracker.add_frame_time(16.0)
    runtime = _runtime_factice(tracker)
    await _surveiller_source_fps(runtime, delai=0.02)

    assert capsys.readouterr().err == ""


@pytest.mark.parametrize(
    "modifier",
    [
        lambda r: setattr(r, "tracker", None),
        lambda r: setattr(r, "frame_source", None),
    ],
)
async def test_surveillance_inactive_sans_tracker_ou_source(capsys, modifier):
    """Mode fps "off" ou "push" : rien a surveiller, et surtout pas d'attente inutile."""
    from overlay.cli import _surveiller_source_fps
    from overlay.fps.tracker import FrameTimeTracker

    runtime = _runtime_factice(FrameTimeTracker(window_seconds=5.0))
    modifier(runtime)
    await _surveiller_source_fps(runtime, delai=999.0)  # ne doit jamais attendre

    assert capsys.readouterr().err == ""


# --- Ctrl-C et fenetre d'appairage depuis l'icone de zone de notification --
#
# `QApplication.exec()` bloque l'interpreteur dans la boucle d'evenements native
# de Qt : sans le correctif verifie ici, un signal SIGINT recu pendant que
# l'overlay tourne ne fait tout simplement rien tant qu'aucun evenement Qt ne
# rend la main a Python (au mieux avec un delai imprevisible, au pire jamais).


def test_permettre_ctrl_c_installe_un_gestionnaire_qui_quitte_l_application():
    pytest.importorskip("PySide6")
    import os
    import signal

    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
    from PySide6.QtWidgets import QApplication

    from overlay.cli import _permettre_ctrl_c

    application = QApplication.instance() or QApplication([])
    gestionnaire_original = signal.getsignal(signal.SIGINT)
    appels: list = []
    application.quit = lambda: appels.append(True)  # evite de vraiment arreter Qt
    try:
        veille = _permettre_ctrl_c(application)
        assert veille.isActive()
        signal.getsignal(signal.SIGINT)(signal.SIGINT, None)
        assert appels == [True]
    finally:
        signal.signal(signal.SIGINT, gestionnaire_original)


def test_dialogue_appairage_recoit_les_bonnes_adresses(monkeypatch):
    pytest.importorskip("PySide6")
    import os

    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
    from PySide6.QtWidgets import QApplication

    from overlay import cli as cli_mod
    from overlay.runtime import Runtime

    QApplication.instance() or QApplication([])

    captures: dict = {}

    class _DialogueFactice:
        def __init__(self, resume, matrice):
            captures["resume"] = resume
            captures["matrice"] = matrice

        def exec(self):
            captures["exec_appele"] = True

    monkeypatch.setattr("overlay.overlay.tray.DialogueAppairage", _DialogueFactice)

    config = Config()
    config.server.port = 8777
    runtime = Runtime(
        config=config,
        hub=None,
        tracker=None,
        frame_source=None,
        token="jeton-abc",
    )
    cli_mod._ouvrir_dialogue_appairage(runtime)

    assert captures["exec_appele"] is True
    assert "jeton-abc" in captures["resume"]["primary_url"]
