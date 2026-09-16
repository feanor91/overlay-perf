import json

import pytest

from overmlay.cli import build_parser, main
from overmlay.config import Config
from overmlay.runtime import build_runtime


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
