import pytest

from overlay.config import (
    DEFAULT_OVERLAY_METRICS,
    Config,
    ConfigError,
    load_config,
    resolve_token,
)


def ecrire(tmp_path, contenu):
    chemin = tmp_path / "config.toml"
    chemin.write_text(contenu, encoding="utf-8")
    return chemin


def test_fichier_absent_donne_les_valeurs_par_defaut(tmp_path):
    config = load_config(tmp_path / "rien.toml")
    assert config.server.port == 8777
    assert config.general.poll_interval == 1.0
    assert tuple(config.overlay.metrics) == DEFAULT_OVERLAY_METRICS


@pytest.mark.parametrize("cle", ["cpu.power", "gpu.0.power"])
def test_les_consommations_sont_affichees_par_defaut(cle):
    """Une cle unique par composant, quel que soit le fabricant ou la source."""
    assert cle in DEFAULT_OVERLAY_METRICS
    assert cle in Config().overlay.metrics

    from overlay.cli import EXEMPLE_CONFIG

    assert cle in load_config(EXEMPLE_CONFIG).overlay.metrics


def test_le_modele_livre_est_valide():
    from overlay.cli import EXEMPLE_CONFIG

    config = load_config(EXEMPLE_CONFIG)
    assert config.overlay.hotkey
    assert config.fps.mode == "auto"


def test_raccourci_de_sortie_et_icone_actifs_par_defaut():
    """Complement de Ctrl-C : un raccourci global pour quitter, sans terminal."""
    config = Config()
    assert config.overlay.hotkey_quit
    assert config.overlay.hotkey_quit != config.overlay.hotkey
    assert config.overlay.tray_icon is True


def test_surcharge_partielle(tmp_path):
    config = load_config(
        ecrire(
            tmp_path,
            """
            [general]
            poll_interval = 0.5
            [overlay]
            position = "bottom-right"
            metrics = ["fps.current", "cpu.load"]
            """,
        )
    )
    assert config.general.poll_interval == 0.5
    assert config.overlay.position == "bottom-right"
    assert config.overlay.metrics == ["fps.current", "cpu.load"]
    assert config.overlay.opacity == 0.85  # non mentionne : valeur par defaut


def test_entier_accepte_pour_un_flottant(tmp_path):
    config = load_config(ecrire(tmp_path, "[general]\npoll_interval = 2"))
    assert config.general.poll_interval == 2.0
    assert isinstance(config.general.poll_interval, float)


@pytest.mark.parametrize(
    ("contenu", "fragment"),
    [
        ('[overlay]\nposition = "milieu"', "position"),
        ("[general]\npoll_interval = 0", "poll_interval"),
        ("[general]\npoll_interval = -1", "poll_interval"),
        ("[general]\nhistory_size = 0", "history_size"),
        ('[fps]\nmode = "magique"', "mode"),
        ("[server]\nport = 99999", "port"),
        ("[server]\nport = 0", "port"),
        ("[overlay]\nopacity = 2.0", "opacity"),
        ("[overlay]\ncolumns = 0", "columns"),
        ('[general]\nmock = "oui"', "booleen"),
        ("[server]\nport = true", "nombre"),
        ("[overlay]\nmetrics = [1, 2]", "liste de chaines"),
        ("[general]\ninconnu = 1", "option inconnue"),
        ("[section_inconnue]\nx = 1", "Sections inconnues"),
        ('[general]\npoll_interval = "vite"', "nombre"),
    ],
)
def test_configurations_invalides_rejetees(tmp_path, contenu, fragment):
    with pytest.raises(ConfigError, match=fragment):
        load_config(ecrire(tmp_path, contenu))


def test_section_qui_n_est_pas_une_table(tmp_path):
    with pytest.raises(ConfigError, match="table TOML"):
        load_config(ecrire(tmp_path, "general = 3"))


def test_jeton_du_fichier_prioritaire():
    config = Config()
    config.server.token = "jeton-explicite"
    assert resolve_token(config) == "jeton-explicite"


def test_jeton_genere_puis_reutilise(tmp_path, monkeypatch):
    monkeypatch.setattr("overlay.config.data_path", lambda: tmp_path)
    config = Config()
    premier = resolve_token(config)
    assert len(premier) >= 24
    assert resolve_token(config) == premier  # persiste d'une session a l'autre
    assert (tmp_path / "token").read_text(encoding="utf-8") == premier


def test_jeton_non_cree_a_la_demande(tmp_path, monkeypatch):
    monkeypatch.setattr("overlay.config.data_path", lambda: tmp_path)
    assert resolve_token(Config(), create=False) == ""
    assert not (tmp_path / "token").exists()
