"""Tests de l'overlay : PySide6 est optionnel, ils sont sautes s'il est absent."""

import os

import pytest

pytest.importorskip("PySide6", reason="dependance optionnelle [overlay]")
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtWidgets import QApplication  # noqa: E402

from overlay.config import OverlayConfig  # noqa: E402
from overlay.models import Group, Kind, Snapshot  # noqa: E402
from overlay.overlay.hotkeys import HotkeyListener  # noqa: E402
from overlay.overlay.window import (  # noqa: E402
    COULEUR_CHAUD,
    COULEUR_OK,
    COULEUR_TIEDE,
    COULEUR_VALEUR,
    OverlayWindow,
    couleur_valeur,
    formater_valeur,
)
from tests.helpers import reading  # noqa: E402


@pytest.fixture(scope="module")
def application():
    return QApplication.instance() or QApplication([])


@pytest.mark.parametrize(
    ("valeur", "attendu"),
    [(None, "—"), (61.25, "61.2 %"), (100.0, "100 %"), (4368.9, "4 369 %"), (0.0, "0.0 %")],
)
def test_mise_en_forme_des_valeurs(valeur, attendu):
    assert formater_valeur(reading("x", valeur)) == attendu


# --- RAM et VRAM : "utilise / total" plutot qu'un chiffre brut sans repere -


def test_memoire_affiche_utilise_sur_total_en_gio():
    vram = reading(
        "gpu.0.vram.used", 2447.0, unit="MiB", kind=Kind.MEMORY, minimum=0.0, maximum=16384.0
    )
    assert formater_valeur(vram) == "2.4 / 16.0 Go"


def test_memoire_convertit_depuis_gio_sans_double_conversion():
    """LibreHardwareMonitor annonce certaines sondes memoire directement en GiB."""
    vram = reading(
        "gpu.0.vram.used", 8.0, unit="GiB", kind=Kind.MEMORY, minimum=0.0, maximum=16.0
    )
    assert formater_valeur(vram) == "8.0 / 16.0 Go"


def test_memoire_sans_total_connu_retombe_sur_le_format_brut():
    """Sans `maximum`, rien pour calculer un total : le chiffre brut reste affiche."""
    vram = reading("gpu.0.vram.used", 2447.0, unit="MiB", kind=Kind.MEMORY)
    assert formater_valeur(vram) == "2 447 MiB"


def test_memoire_totale_ne_s_affiche_pas_comme_utilise_sur_total():
    """value == maximum (la sonde memoire.total elle-meme) : pas de doublon absurde."""
    total = reading(
        "memory.total", 32768.0, unit="MiB", kind=Kind.MEMORY, minimum=0.0, maximum=32768.0
    )
    assert formater_valeur(total) == "32 768 MiB"


@pytest.mark.parametrize(
    ("valeur", "couleur"),
    [(30.0, COULEUR_OK), (70.0, COULEUR_TIEDE), (95.0, COULEUR_CHAUD)],
)
def test_couleur_selon_le_seuil(valeur, couleur):
    assert couleur_valeur(reading("cpu.load", valeur)) == couleur


def test_les_mesures_sans_seuil_restent_neutres():
    horloge = reading("cpu.clock", 4200.0, kind=Kind.FREQUENCY, unit="MHz")
    assert couleur_valeur(horloge) == COULEUR_VALEUR


def test_affichage_des_mesures_selectionnees(application):
    config = OverlayConfig(metrics=["cpu.load", "gpu.*"], columns=1)
    fenetre = OverlayWindow(config)
    fenetre.appliquer(
        Snapshot.build(
            [
                reading("cpu.load", 42.0),
                reading("memory.load", 80.0, group=Group.MEMORY),
                reading("gpu.0.temp", 61.0, group=Group.GPU, kind=Kind.TEMPERATURE),
            ]
        )
    )
    assert [r.key for r in fenetre._readings] == ["cpu.load", "gpu.0.temp"]
    largeur, hauteur, *_ = fenetre._dimensions()
    assert largeur > 0 and hauteur > 0


def test_les_mesures_absentes_ne_sont_pas_affichees(application):
    fenetre = OverlayWindow(OverlayConfig(metrics=["fps.current", "cpu.load"]))
    fenetre.appliquer(
        Snapshot.build([reading("fps.current", None), reading("cpu.load", 10.0)])
    )
    assert [r.key for r in fenetre._readings] == ["cpu.load"]


def test_disposition_multi_colonnes(application):
    mesures = [reading(f"cpu.core.{i}.load", float(i)) for i in range(6)]
    une = OverlayWindow(OverlayConfig(metrics=["cpu.*"], columns=1))
    trois = OverlayWindow(OverlayConfig(metrics=["cpu.*"], columns=3))
    une.appliquer(Snapshot.build(mesures))
    trois.appliquer(Snapshot.build(mesures))
    assert trois._dimensions()[4] == 2   # 6 lignes sur 3 colonnes
    assert une._dimensions()[4] == 6
    assert trois._dimensions()[1] < une._dimensions()[1]  # plus large, moins haut


def test_basculement_de_visibilite(application):
    fenetre = OverlayWindow(OverlayConfig(metrics=["cpu.load"]))
    fenetre.appliquer(Snapshot.build([reading("cpu.load", 10.0)]))
    assert not fenetre.isVisible()
    fenetre.basculer()
    assert fenetre.isVisible()
    fenetre.basculer()
    assert not fenetre.isVisible()


def test_le_rendu_ne_leve_pas(application):
    fenetre = OverlayWindow(OverlayConfig(metrics=["cpu.*", "gpu.*"], columns=2))
    fenetre.appliquer(
        Snapshot.build(
            [
                reading("cpu.load", 42.0),
                reading("cpu.temp", 88.0, kind=Kind.TEMPERATURE, unit="°C"),
                reading("gpu.0.load", 99.0, group=Group.GPU),
            ]
        )
    )
    fenetre.show()
    image = fenetre.grab()
    assert image.width() > 0 and image.height() > 0


def test_overlay_vide_ne_plante_pas(application):
    fenetre = OverlayWindow(OverlayConfig(metrics=["inexistant"]))
    fenetre.appliquer(Snapshot.build([reading("cpu.load", 1.0)]))
    assert fenetre._readings == ()
    assert fenetre._dimensions() == (0, 0, 0, 0, 0)
    fenetre.show()
    fenetre.grab()


def test_raccourci_vide_inactif():
    assert HotkeyListener("", lambda: None).start() is False


def test_raccourci_sans_pynput(monkeypatch):
    import builtins

    original = builtins.__import__

    def refuser(nom, *args, **kwargs):
        if nom.startswith("pynput"):
            raise ImportError("absent")
        return original(nom, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", refuser)
    assert HotkeyListener("<ctrl>+<alt>+o", lambda: None).start() is False


# --- Icone de zone de notification ------------------------------------------
#
# La plateforme de rendu "offscreen" utilisee par ces tests n'a pas de zone de
# notification : QSystemTrayIcon.isSystemTrayAvailable() y repond toujours False.
# C'est exactement le cas (bureau minimaliste sans zone de notification) que
# `creer_icone_systeme` doit degrader en silence plutot que de faire planter
# l'overlay : c'est ce que ces tests verifient reellement ici.


def test_icone_systeme_absente_sans_zone_de_notification(application):
    from overlay.overlay.tray import creer_icone_systeme

    assert (
        creer_icone_systeme(basculer_overlay=lambda: None, quitter=lambda: None) is None
    )


def test_rendu_pixel_du_qr_code(application):
    from overlay.overlay.tray import _pixmap_qr

    matrice = [[True, False], [False, True]]
    pixmap = _pixmap_qr(matrice, echelle=5)
    assert pixmap.width() == 10 and pixmap.height() == 10
    assert not pixmap.isNull()


def test_dialogue_appairage_sans_qrcode_affiche_l_astuce(application):
    from PySide6.QtWidgets import QLabel

    from overlay.overlay.tray import DialogueAppairage

    resume = {"primary_url": "http://192.168.1.2:8777/#token=abc", "urls": [], "token": "abc"}
    dialogue = DialogueAppairage(resume, None)
    textes = [label.text() for label in dialogue.findChildren(QLabel)]
    assert any("qrcode" in texte for texte in textes)
    assert any("192.168.1.2:8777" in texte for texte in textes)
    dialogue.deleteLater()


def test_dialogue_appairage_avec_qrcode_affiche_l_image(application):
    from overlay.overlay.tray import DialogueAppairage

    resume = {
        "primary_url": "http://192.168.1.2:8777/#token=abc",
        "urls": ["http://192.168.1.2:8777/#token=abc"],
        "token": "abc",
    }
    dialogue = DialogueAppairage(resume, [[True, False], [False, True]])
    assert dialogue.windowTitle() == "Appairer un telephone"
    dialogue.deleteLater()
