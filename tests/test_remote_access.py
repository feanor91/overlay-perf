"""Acces a l'agent depuis un autre reseau : verrouillage, tunnel, appairage."""

import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from overmlay.config import Config, ConfigError, load_config, resolve_token, rotate_token
from overmlay.hub import MetricsHub
from overmlay.server.app import WS_POLICY_VIOLATION, WS_TRY_LATER, create_app
from overmlay.server.auth import AuthThrottle, client_address
from overmlay.server.pairing import pairing_summary, pairing_url, with_token
from tests.helpers import StaticBackend, reading

JETON = "jeton-de-test"


def construire_client(**kwargs):
    hub = MetricsHub([StaticBackend([reading("cpu.load", 42.0)])], poll_interval=0.05)
    return TestClient(create_app(hub, token=JETON, **kwargs))


# --- Verrouillage apres echecs ---------------------------------------------


def test_quelques_erreurs_de_frappe_ne_verrouillent_pas():
    limiteur = AuthThrottle(max_failures=5, window=300.0, lockout=60.0)
    for _ in range(4):
        limiteur.record_failure("203.0.113.7")
    assert limiteur.retry_after("203.0.113.7") == 0


def test_une_salve_verrouille_l_adresse():
    limiteur = AuthThrottle(max_failures=3, window=300.0, lockout=60.0)
    for _ in range(3):
        limiteur.record_failure("203.0.113.7")
    assert limiteur.retry_after("203.0.113.7") == pytest.approx(60.0, abs=1.0)
    # Le verrou ne vise que l'attaquant : les autres clients passent toujours.
    assert limiteur.retry_after("198.51.100.4") == 0


def test_le_verrou_expire():
    limiteur = AuthThrottle(max_failures=1, lockout=0.2)
    limiteur.record_failure("203.0.113.7")
    assert limiteur.retry_after("203.0.113.7") > 0
    time.sleep(0.25)
    assert limiteur.retry_after("203.0.113.7") == 0


def test_une_authentification_reussie_efface_l_ardoise():
    limiteur = AuthThrottle(max_failures=3)
    limiteur.record_failure("203.0.113.7")
    limiteur.record_failure("203.0.113.7")
    limiteur.record_success("203.0.113.7")
    limiteur.record_failure("203.0.113.7")
    assert limiteur.retry_after("203.0.113.7") == 0


def test_les_echecs_anciens_sortent_de_la_fenetre():
    limiteur = AuthThrottle(max_failures=3, window=0.2, lockout=60.0)
    limiteur.record_failure("203.0.113.7")
    limiteur.record_failure("203.0.113.7")
    time.sleep(0.25)
    limiteur.record_failure("203.0.113.7")
    assert limiteur.retry_after("203.0.113.7") == 0


def test_la_table_ne_grossit_pas_indefiniment():
    """Un balayage d'adresses ne doit pas faire enfler la memoire de l'agent."""
    limiteur = AuthThrottle(max_failures=100, window=300.0, max_tracked=50)
    for octet in range(200):
        limiteur.record_failure(f"203.0.113.{octet}")
    assert len(limiteur._failures) <= 51


def test_client_inconnu_ignore():
    limiteur = AuthThrottle(max_failures=1)
    limiteur.record_failure(None)
    assert limiteur.retry_after(None) == 0


# --- Application du verrou par l'API ---------------------------------------


def test_l_api_repond_429_apres_trop_d_echecs():
    with construire_client(throttle=AuthThrottle(max_failures=3, lockout=60.0)) as client:
        faux = {"Authorization": "Bearer faux"}
        assert [client.get("/api/metrics", headers=faux).status_code for _ in range(3)] == [
            401,
            401,
            401,
        ]
        refus = client.get("/api/metrics", headers=faux)
        assert refus.status_code == 429
        assert int(refus.headers["Retry-After"]) > 0
        # Le verrou porte sur l'adresse, pas sur le jeton presente.
        bon = {"Authorization": f"Bearer {JETON}"}
        assert client.get("/api/metrics", headers=bon).status_code == 429


def test_le_websocket_applique_le_meme_verrou():
    from starlette.websockets import WebSocketDisconnect

    with construire_client(throttle=AuthThrottle(max_failures=2, lockout=60.0)) as client:
        for _ in range(2):
            client.get("/api/metrics", headers={"Authorization": "Bearer faux"})
        with pytest.raises(WebSocketDisconnect) as erreur, client.websocket_connect(
            f"/ws?token={JETON}"
        ) as ws:
            ws.receive_json()
    assert erreur.value.code == WS_TRY_LATER


def test_un_jeton_invalide_sur_le_websocket_compte_comme_un_echec():
    from starlette.websockets import WebSocketDisconnect

    limiteur = AuthThrottle(max_failures=2, lockout=60.0)
    with construire_client(throttle=limiteur) as client:
        # Le verrou s'engage apres le deuxieme echec : la troisieme tentative est
        # refusee avant meme que le jeton soit examine.
        for attendu in (WS_POLICY_VIOLATION, WS_POLICY_VIOLATION, WS_TRY_LATER):
            with pytest.raises(WebSocketDisconnect) as erreur, client.websocket_connect(
                "/ws?token=faux"
            ) as ws:
                ws.receive_json()
            assert erreur.value.code == attendu


def test_sans_verrouillage_configure_le_comportement_reste_inchange():
    with construire_client() as client:
        codes = {
            client.get("/api/metrics", headers={"Authorization": "Bearer faux"}).status_code
            for _ in range(5)
        }
        assert codes == {401}


# --- Identification du client derriere un tunnel ---------------------------


def test_l_en_tete_transmise_est_ignoree_sans_proxy_declare():
    """Sinon n'importe qui echapperait au verrou en variant X-Forwarded-For."""
    assert client_address(("203.0.113.9", 5000), {"X-Forwarded-For": "1.1.1.1"}, False) == (
        "203.0.113.9"
    )


def test_l_en_tete_transmise_est_lue_derriere_un_proxy_de_confiance():
    adresse = client_address(("127.0.0.1", 5000), {"X-Forwarded-For": "1.1.1.1, 10.0.0.1"}, True)
    assert adresse == "1.1.1.1"


def test_client_absent_du_scope():
    assert client_address(None, {}, False) is None


def test_le_verrou_distingue_les_clients_du_meme_tunnel():
    """Tous les clients d'un tunnel partagent l'IP du proxy : sans en-tete, un seul
    attaquant verrouillerait tout le monde."""
    limiteur = AuthThrottle(max_failures=2, lockout=60.0)
    with construire_client(throttle=limiteur, trust_proxy=True) as client:
        attaquant = {"Authorization": "Bearer faux", "X-Forwarded-For": "203.0.113.9"}
        for _ in range(2):
            client.get("/api/metrics", headers=attaquant)
        assert client.get("/api/metrics", headers=attaquant).status_code == 429

        legitime = {"Authorization": f"Bearer {JETON}", "X-Forwarded-For": "198.51.100.4"}
        assert client.get("/api/metrics", headers=legitime).status_code == 200


# --- Configuration ---------------------------------------------------------


@pytest.mark.parametrize(
    ("contenu", "fragment"),
    [
        ('[server]\npublic_url = "pc.exemple.fr"', "public_url"),
        ('[server]\ntls_cert = "/absent.pem"', "vont de pair"),
        ('[server]\nmax_auth_failures = 0', "max_auth_failures"),
        ('[server]\nauth_lockout_seconds = 0', "auth_lockout_seconds"),
    ],
)
def test_configurations_d_acces_distant_invalides(tmp_path, contenu, fragment):
    chemin = tmp_path / "config.toml"
    chemin.write_text(contenu, encoding="utf-8")
    with pytest.raises(ConfigError, match=fragment):
        load_config(chemin)


def test_certificat_tls_inexistant_refuse(tmp_path):
    chemin = tmp_path / "config.toml"
    # Barres obliques : sous Windows, « C:\Users\... » dans une chaine TOML serait
    # lu comme une sequence d'echappement. pathlib accepte les deux separateurs.
    absent_cert = (tmp_path / "absent.pem").as_posix()
    absent_cle = (tmp_path / "absent.key").as_posix()
    chemin.write_text(
        f'[server]\ntls_cert = "{absent_cert}"\ntls_key = "{absent_cle}"',
        encoding="utf-8",
    )
    with pytest.raises(ConfigError, match="fichier introuvable"):
        load_config(chemin)


def test_certificat_tls_present_accepte(tmp_path):
    cert = tmp_path / "cert.pem"
    cle = tmp_path / "cert.key"
    cert.write_text("-----BEGIN CERTIFICATE-----", encoding="utf-8")
    cle.write_text("-----BEGIN PRIVATE KEY-----", encoding="utf-8")
    chemin = tmp_path / "config.toml"
    chemin.write_text(
        f'[server]\ntls_cert = "{cert.as_posix()}"\ntls_key = "{cle.as_posix()}"',
        encoding="utf-8",
    )
    assert Path(load_config(chemin).server.tls_cert) == cert


# --- Appairage -------------------------------------------------------------


def test_le_qr_pointe_sur_l_adresse_publique_quand_elle_existe():
    resume = pairing_summary(8777, "abc", public_url="https://pc.exemple.fr")
    assert resume["primary_url"] == "https://pc.exemple.fr/#token=abc"
    assert resume["remote_url"] == "https://pc.exemple.fr/#token=abc"
    # L'adresse locale reste proposee : sur place, elle evite le detour par Internet.
    assert resume["local_url"].startswith("http://")


def test_sans_adresse_publique_le_qr_reste_local():
    resume = pairing_summary(8777, "abc")
    assert resume["remote_url"] == ""
    assert resume["primary_url"] == resume["local_url"]


def test_l_appairage_suit_le_tls_direct():
    assert pairing_url("192.168.1.2", 8777, "x", scheme="https").startswith("https://")


def test_jeton_ajoute_a_une_url_de_tunnel():
    assert with_token("https://pc.exemple.fr", "a/b") == "https://pc.exemple.fr/#token=a%2Fb"
    assert with_token("https://pc.exemple.fr/", "") == "https://pc.exemple.fr/"


# --- Rotation du jeton -----------------------------------------------------


def test_rotation_du_jeton(tmp_path, monkeypatch):
    monkeypatch.setattr("overmlay.config.data_path", lambda: tmp_path)
    config = Config()
    premier = resolve_token(config)
    nouveau = rotate_token(config)
    assert nouveau != premier
    assert resolve_token(config) == nouveau


def test_rotation_refusee_si_le_jeton_est_fixe_dans_la_configuration():
    config = Config()
    config.server.token = "fixe-a-la-main"
    with pytest.raises(ConfigError, match="fixe dans la configuration"):
        rotate_token(config)
