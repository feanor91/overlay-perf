import pytest
from fastapi.testclient import TestClient

from overlay.fps.tracker import FrameTimeTracker
from overlay.hub import MetricsHub
from overlay.server.app import create_app
from overlay.server.auth import extract_token, token_matches
from overlay.server.pairing import local_ip_addresses, pairing_url, qr_matrix, render_qr
from tests.helpers import StaticBackend, reading

JETON = "jeton-de-test"


@pytest.fixture
def client():
    hub = MetricsHub(
        [
            StaticBackend(
                [
                    reading("cpu.load", 42.0),
                    reading("gpu.0.temp", 61.0),
                    reading("gpu.0.load", 77.0),
                ]
            )
        ],
        poll_interval=0.05,
    )
    tracker = FrameTimeTracker(window_seconds=5.0, stale_after=60.0)
    with TestClient(create_app(hub, token=JETON, tracker=tracker)) as testeur:
        testeur.tracker = tracker
        yield testeur


@pytest.fixture
def entetes():
    return {"Authorization": f"Bearer {JETON}"}


# --- Jeton -----------------------------------------------------------------


@pytest.mark.parametrize(
    ("entetes", "requete", "attendu"),
    [
        ({"Authorization": "Bearer abc"}, {}, "abc"),
        ({"authorization": "bearer abc"}, {}, "abc"),
        ({"X-Overlay-Token": "def"}, {}, "def"),
        ({}, {"token": "ghi"}, "ghi"),
        ({}, {}, None),
        ({"Authorization": "Bearer "}, {}, None),
    ],
)
def test_extraction_du_jeton(entetes, requete, attendu):
    assert extract_token(entetes, requete) == attendu


def test_comparaison_du_jeton():
    assert token_matches("secret", "secret")
    assert not token_matches("secret", "autre")
    assert not token_matches("secret", None)
    assert token_matches("", "peu importe")  # authentification desactivee


# --- Routes ----------------------------------------------------------------


def test_sonde_de_sante_publique(client):
    corps = client.get("/api/health").json()
    assert corps["service"] == "overlay"
    assert corps["auth_required"] is True


@pytest.mark.parametrize(
    "chemin", ["/api/metrics", "/api/sensors", "/api/history", "/api/fps"]
)
def test_routes_protegees(client, chemin):
    assert client.get(chemin).status_code == 401
    assert client.get(chemin, headers={"Authorization": "Bearer faux"}).status_code == 401


@pytest.mark.parametrize(
    ("entetes", "requete"),
    [
        ({"Authorization": f"Bearer {JETON}"}, ""),
        ({"X-Overlay-Token": JETON}, ""),
        ({}, f"?token={JETON}"),
    ],
)
def test_jeton_accepte_dans_les_trois_emplacements(client, entetes, requete):
    assert client.get(f"/api/metrics{requete}", headers=entetes).status_code == 200


def test_mesures_courantes(client, entetes):
    corps = client.get("/api/metrics", headers=entetes).json()
    assert corps["host"]
    assert {r["key"] for r in corps["readings"]} == {"cpu.load", "gpu.0.temp", "gpu.0.load"}
    assert corps["readings"][0]["gauge"] is True


def test_filtrage_par_cles(client, entetes):
    corps = client.get("/api/metrics?keys=gpu.*", headers=entetes).json()
    assert {r["key"] for r in corps["readings"]} == {"gpu.0.temp", "gpu.0.load"}


def test_catalogue_des_capteurs(client, entetes):
    corps = client.get("/api/sensors", headers=entetes).json()
    assert corps["backends"][0]["name"] == "static"
    assert {m["key"] for m in corps["metrics"]} >= {"cpu.load"}


def test_historique(client, entetes):
    corps = client.get("/api/history?limit=5&keys=cpu.load", headers=entetes).json()
    assert 1 <= len(corps["snapshots"]) <= 5
    assert corps["snapshots"][0]["readings"][0]["key"] == "cpu.load"


def test_historique_borne_les_limites(client, entetes):
    assert client.get("/api/history?limit=0", headers=entetes).status_code == 422
    assert client.get("/api/history?limit=99999", headers=entetes).status_code == 422


# --- FPS -------------------------------------------------------------------


def test_poussee_de_trames(client, entetes):
    for _ in range(30):
        reponse = client.post(
            "/api/fps/frame",
            json={"frame_time_ms": 8.33, "application": "jeu.exe"},
            headers=entetes,
        )
        assert reponse.status_code == 202
    corps = client.get("/api/fps", headers=entetes).json()
    assert corps["fps"] == pytest.approx(120.0, abs=1.0)
    assert corps["application"] == "jeu.exe"
    assert corps["stale"] is False


def test_poussee_par_horodatage(client, entetes):
    for index in range(6):
        client.post("/api/fps/frame", json={"timestamp": 500.0 + index * 0.01}, headers=entetes)
    assert client.get("/api/fps", headers=entetes).json()["frames"] == 5


@pytest.mark.parametrize(
    "charge", [{"frame_time_ms": 0}, {"frame_time_ms": -3}, {"frame_time_ms": 99999}]
)
def test_durees_de_trame_aberrantes_refusees(client, entetes, charge):
    assert client.post("/api/fps/frame", json=charge, headers=entetes).status_code == 422


def test_fps_desactive():
    hub = MetricsHub([StaticBackend([reading("cpu.load")])], poll_interval=0.05)
    with TestClient(create_app(hub, token="", tracker=None)) as client:
        assert client.get("/api/fps").status_code == 503
        assert client.post("/api/fps/frame", json={"frame_time_ms": 8.0}).status_code == 503


# --- WebSocket -------------------------------------------------------------


def test_flux_temps_reel(client):
    with client.websocket_connect(f"/ws?token={JETON}") as ws:
        premier = ws.receive_json()
        assert {r["key"] for r in premier["readings"]} >= {"cpu.load"}
        second = ws.receive_json()
        assert second["timestamp"] >= premier["timestamp"]


def test_flux_filtre(client):
    with client.websocket_connect(f"/ws?token={JETON}&keys=gpu.0.temp") as ws:
        assert [r["key"] for r in ws.receive_json()["readings"]] == ["gpu.0.temp"]


def test_flux_refuse_sans_jeton(client):
    from starlette.websockets import WebSocketDisconnect

    with pytest.raises(WebSocketDisconnect) as erreur, client.websocket_connect(
        "/ws?token=faux"
    ) as ws:
        ws.receive_json()
    assert erreur.value.code == 1008


def test_deconnexion_libere_l_abonnement(client):
    hub = client.app.state.hub
    with client.websocket_connect(f"/ws?token={JETON}"):
        pass
    # Le desabonnement a lieu dans la tache serveur : on laisse passer un cycle.
    client.get("/api/health")
    assert hub.subscriber_count <= 1


# --- Application mobile ----------------------------------------------------


def test_la_page_est_servie(client):
    reponse = client.get("/")
    assert reponse.status_code == 200
    assert "Overlay" in reponse.text


@pytest.mark.parametrize(
    "fichier", ["style.css", "app.js", "manifest.webmanifest", "sw.js", "icon-192.png"]
)
def test_ressources_statiques(client, fichier):
    assert client.get(f"/app/{fichier}").status_code == 200


# --- Appairage -------------------------------------------------------------


def test_adresses_locales():
    adresses = local_ip_addresses()
    assert adresses
    assert all(not a.startswith("127.") for a in adresses) or adresses == ["127.0.0.1"]


def test_url_d_appairage():
    assert pairing_url("192.168.1.2", 8777) == "http://192.168.1.2:8777/"
    # Le jeton passe dans le fragment, jamais dans la chaine de requete.
    assert pairing_url("192.168.1.2", 8777, "a/b+c") == "http://192.168.1.2:8777/#token=a%2Fb%2Bc"


def test_qr_code():
    rendu = render_qr("http://192.168.1.2:8777/")
    assert rendu is not None
    lignes = rendu.splitlines()
    assert len(lignes) > 8
    assert all(len(ligne) == len(lignes[0]) for ligne in lignes)


def test_qr_matrix_alimente_le_rendu_terminal():
    """`render_qr` et l'icone de zone de notification partagent la meme grille."""
    matrice = qr_matrix("http://192.168.1.2:8777/")
    assert matrice is not None
    assert len(matrice) > 8
    assert all(len(ligne) == len(matrice[0]) for ligne in matrice)
    assert all(isinstance(cellule, bool) for ligne in matrice for cellule in ligne)
