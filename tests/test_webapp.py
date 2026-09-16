"""Logique de connexion de l'application mobile, executee sous Node.

`app.js` tourne dans un navigateur : ces tests le chargent dans un contexte Node
muni d'un DOM, d'un stockage et d'un WebSocket factices (`tests/webapp/`). Ils sont
sautes si Node n'est pas installe.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

import pytest

NODE = shutil.which("node")
SCENARIOS = Path(__file__).parent / "webapp" / "scenarios.mjs"

pytestmark = pytest.mark.skipif(NODE is None, reason="Node absent : logique JS non testee")


def scenario(nom: str) -> dict:
    resultat = subprocess.run(  # noqa: S603 - binaire resolu via shutil.which
        [NODE, str(SCENARIOS), nom],
        capture_output=True,
        text=True,
        # Sans encodage explicite, Python decode avec celui du systeme : sous
        # Windows (cp1252) les accents et le « · » de l'interface sont mutiles.
        encoding="utf-8",
        timeout=30,
        check=False,
    )
    assert resultat.returncode == 0, resultat.stderr
    return json.loads(resultat.stdout)


def test_les_adresses_sont_dedoublonnees_et_nettoyees():
    resultat = scenario("normalisation")
    assert resultat["doublons"] == ["http://a:1"]  # barre finale et espaces ignores
    assert resultat["vides"] == ["http://b:2"]
    assert resultat["ordre"] == ["http://local", "https://distant"]  # ordre preserve


def test_le_websocket_suit_le_protocole_de_l_adresse():
    resultat = scenario("schemaWebsocket")
    assert resultat["clair"] == "ws://192.168.1.42:8777/ws?token=abc"
    # Une adresse distante en HTTPS impose un WebSocket chiffre.
    assert resultat["chiffre"] == "wss://pc.exemple.fr/ws?token=a%2Fb%2Bc"
    assert resultat["sansJeton"] == "ws://192.168.1.42:8777/ws"


@pytest.mark.parametrize("cas", ["relative", "vide", "mauvaisSchema"])
def test_adresse_incomplete_refusee(cas):
    """Resolue contre l'origine, « mon-pc » pointerait en douce sur la machine locale."""
    assert scenario("schemaWebsocket")[cas] is None


def test_migration_depuis_l_ancien_format():
    """Les versions anterieures ne stockaient qu'une adresse, sous la cle `url`."""
    resultat = scenario("migrationAncienFormat")
    assert resultat["urls"] == ["http://192.168.1.42:8777"]
    assert resultat["token"] == "vieux"


def test_le_qr_distant_s_ajoute_sans_effacer_l_adresse_locale():
    """Scanner le QR du tunnel doit completer la configuration, pas la remplacer."""
    resultat = scenario("jetonDansLeFragment")
    assert resultat["urls"] == ["https://pc.exemple.fr", "http://192.168.1.42:8777"]
    assert resultat["token"] == "nouveau"
    assert resultat["persiste"]["urls"] == resultat["urls"]


def test_bascule_du_local_vers_le_distant():
    resultat = scenario("basculeVersLAdresseDistante")
    premiere, seconde = resultat["etapes"]
    assert premiere["url"].startswith("ws://192.168.1.42:8777")
    assert seconde["url"].startswith("wss://pc.exemple.fr")
    # Le passage a l'adresse suivante est rapide : pas de recul complet entre les deux.
    assert seconde["delai"] == 1000
    assert resultat["etatConnecte"] == "En direct · distant"


def test_le_recul_ne_grandit_qu_apres_un_tour_complet():
    """Essayer les deux adresses doit rester rapide ; c'est le cycle qui s'espace."""
    resultat = scenario("reculExponentielApresUnTourComplet")
    assert resultat["delais"] == [1000, 1000, 1000, 2000, 1000, 4000]
    assert resultat["urls"][:4] == [
        "ws://local:8777/ws",
        "wss://distant/ws",
        "ws://local:8777/ws",
        "wss://distant/ws",
    ]


def test_une_adresse_invalide_n_empeche_pas_l_autre():
    resultat = scenario("adresseInvalidePasseALaSuivante")
    assert resultat["socketsAvant"] == 0  # rien n'est tente sur l'adresse fautive
    assert resultat["url"].startswith("wss://pc.exemple.fr")
    assert resultat["etat"] == "En direct · distant"


def test_un_jeton_refuse_ne_declenche_pas_de_boucle():
    """Reessayer avec le meme jeton est inutile et ressemble a une attaque."""
    resultat = scenario("jetonRefuseNeBoucle")
    assert resultat["reconnexionsProgrammees"] == 0
    assert resultat["etat"] == "Jeton refuse"
    assert "overmlay pair" in resultat["messageAffiche"]


def test_pas_d_etiquette_avec_une_seule_adresse():
    assert scenario("uneSeuleAdresseNAffichePasDEtiquette")["etat"] == "En direct"
