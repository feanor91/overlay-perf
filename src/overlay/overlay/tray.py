"""Icone de zone de notification : appairage du telephone et sortie sans terminal.

`QSystemTrayIcon` fait partie de Qt, donc toujours disponible des que PySide6
l'est ; un bureau sans zone de notification (certains environnements Linux
minimalistes, ou la plateforme de rendu `offscreen` utilisee par les tests)
reste un cas normal, pas une erreur : `creer_icone_systeme` renvoie alors `None`.
"""

from __future__ import annotations

import logging
from collections.abc import Callable
from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtGui import QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (
    QApplication,
    QDialog,
    QLabel,
    QMenu,
    QPushButton,
    QSystemTrayIcon,
    QVBoxLayout,
)

log = logging.getLogger(__name__)

#: Reutilise l'icone deja livree pour l'application mobile (PWA) : meme identite
#: visuelle, aucune ressource binaire supplementaire a ajouter au paquet.
ICONE = Path(__file__).resolve().parent.parent / "webapp" / "icon-192.png"


def creer_icone_systeme(
    *,
    basculer_overlay: Callable[[], None],
    quitter: Callable[[], None],
    afficher_appairage: Callable[[], None] | None = None,
) -> QSystemTrayIcon | None:
    """Cree et affiche l'icone, ou `None` si la zone de notification est indisponible.

    L'appelant doit garder une reference a l'objet retourne : Qt supprime l'icone
    des que le dernier Python la referencant est libere.
    """
    if not QSystemTrayIcon.isSystemTrayAvailable():
        log.info("Zone de notification indisponible sur ce bureau : icone desactivee.")
        return None

    icone = QIcon(str(ICONE)) if ICONE.is_file() else QIcon()
    plateau = QSystemTrayIcon(icone)
    plateau.setToolTip("Overlay")

    menu = QMenu()
    menu.addAction("Afficher / masquer l'overlay", basculer_overlay)
    if afficher_appairage is not None:
        menu.addAction("Appairer un telephone…", afficher_appairage)
    menu.addSeparator()
    menu.addAction("Quitter", quitter)
    plateau.setContextMenu(menu)

    def sur_activation(raison: QSystemTrayIcon.ActivationReason) -> None:
        if raison == QSystemTrayIcon.ActivationReason.Trigger:
            basculer_overlay()

    plateau.activated.connect(sur_activation)
    plateau.show()
    return plateau


def _pixmap_qr(matrice: list[list[bool]], *, echelle: int = 8) -> QPixmap:
    """Rendu pixel du QR code depuis la meme grille que le rendu terminal."""
    cote = len(matrice) * echelle
    pixmap = QPixmap(cote, cote)
    pixmap.fill(Qt.GlobalColor.white)
    peintre = QPainter(pixmap)
    try:
        peintre.setPen(Qt.PenStyle.NoPen)
        peintre.setBrush(Qt.GlobalColor.black)
        for y, ligne in enumerate(matrice):
            for x, rempli in enumerate(ligne):
                if rempli:
                    peintre.drawRect(x * echelle, y * echelle, echelle, echelle)
    finally:
        peintre.end()
    return pixmap


class DialogueAppairage(QDialog):
    """Fenetre minimaliste : QR code (si `qrcode` est installe) et adresses en clair."""

    def __init__(self, resume: dict[str, object], matrice: list[list[bool]] | None) -> None:
        super().__init__()
        self.setWindowTitle("Appairer un telephone")

        agencement = QVBoxLayout(self)

        if matrice is not None:
            qr = QLabel()
            qr.setPixmap(_pixmap_qr(matrice))
            qr.setAlignment(Qt.AlignmentFlag.AlignCenter)
            agencement.addWidget(qr)
        else:
            info = QLabel("(installez « qrcode » pour afficher un QR code a scanner)")
            info.setWordWrap(True)
            agencement.addWidget(info)

        adresse = str(resume["primary_url"])
        texte = QLabel(f"Adresse : {adresse}")
        texte.setWordWrap(True)
        texte.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        agencement.addWidget(texte)

        copier = QPushButton("Copier l'adresse")
        copier.clicked.connect(lambda: QApplication.clipboard().setText(adresse))
        agencement.addWidget(copier)

        fermer = QPushButton("Fermer")
        fermer.clicked.connect(self.accept)
        agencement.addWidget(fermer)
