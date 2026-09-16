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
    runtime = build_runtime(config, need_token=False)
    assert runtime.warnings == ["message de detection"]


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
