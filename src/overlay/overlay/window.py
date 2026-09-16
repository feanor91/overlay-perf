"""Fenetre d'overlay : translucide, toujours au premier plan, transparente aux clics.

Limite connue : un jeu en plein ecran exclusif (DirectX) dessine directement sur
le balayage ecran et masque toute fenetre, overlay compris. En plein ecran fenetre
ou sans bordure (le mode par defaut de la plupart des jeux recents) l'overlay
s'affiche normalement. Le telephone reste de toute facon une sortie utilisable.
"""

from __future__ import annotations

import logging

from PySide6.QtCore import Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QFontMetrics, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QApplication, QWidget

from overlay.config import OverlayConfig
from overlay.models import Kind, Reading, Snapshot

log = logging.getLogger(__name__)

COULEUR_FOND = QColor(8, 11, 18, 190)
COULEUR_LIBELLE = QColor(150, 165, 190)
COULEUR_VALEUR = QColor(235, 240, 248)
COULEUR_OK = QColor(61, 220, 132)
COULEUR_TIEDE = QColor(245, 181, 69)
COULEUR_CHAUD = QColor(255, 107, 94)
COULEUR_CONTOUR = QColor(0, 0, 0, 205)

MARGE_INTERNE = 10
ESPACE_COLONNES = 22


def couleur_valeur(reading: Reading) -> QColor:
    """Vert / orange / rouge selon la position dans la plage utile de la sonde."""
    if reading.value is None:
        return COULEUR_LIBELLE
    if reading.kind not in (Kind.TEMPERATURE, Kind.LOAD, Kind.POWER):
        return COULEUR_VALEUR
    bas, haut = reading.range
    etendue = haut - bas
    if etendue <= 0:
        return COULEUR_VALEUR
    ratio = (reading.value - bas) / etendue
    if ratio >= 0.85:
        return COULEUR_CHAUD
    if ratio >= 0.65:
        return COULEUR_TIEDE
    return COULEUR_OK


def formater_valeur(reading: Reading) -> str:
    if reading.value is None:
        return "—"
    valeur = reading.value
    absolu = abs(valeur)
    if absolu >= 1000:
        texte = f"{valeur:,.0f}".replace(",", " ")
    elif absolu >= 100:
        texte = f"{valeur:.0f}"
    else:
        texte = f"{valeur:.1f}"
    return f"{texte} {reading.unit}".strip()


class OverlayWindow(QWidget):
    """Affichage a l'ecran des mesures selectionnees."""

    #: Emis depuis le thread du raccourci clavier ; Qt le rejoue sur le thread GUI.
    basculement_demande = Signal()

    def __init__(self, config: OverlayConfig, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.config = config
        self._readings: tuple[Reading, ...] = ()

        flags = (
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
            | Qt.WindowType.WindowDoesNotAcceptFocus
        )
        if config.click_through:
            # Souris et clavier traversent la fenetre : le jeu dessous reste jouable.
            flags |= Qt.WindowType.WindowTransparentForInput
        self.setWindowFlags(flags)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating, True)
        self.setWindowOpacity(config.opacity)
        self.setWindowTitle("Overlay")

        police = QFont()
        police.setPointSize(config.font_size)
        police.setWeight(QFont.Weight.DemiBold)
        police.setStyleHint(QFont.StyleHint.SansSerif)
        self.setFont(police)
        self._metrics = QFontMetrics(police)

        self.basculement_demande.connect(self.basculer)

    # --- Donnees -------------------------------------------------------------

    def appliquer(self, snapshot: Snapshot) -> None:
        """Remplace les mesures affichees et redimensionne la fenetre si besoin."""
        selection = snapshot.filter(self.config.metrics)
        nouvelles = tuple(r for r in selection.readings if r.value is not None)
        if nouvelles == self._readings:
            return
        ancienne_disposition = len(self._readings)
        self._readings = nouvelles
        if len(nouvelles) != ancienne_disposition:
            self._ajuster_geometrie()
        self.update()

    def basculer(self) -> None:
        if self.isVisible():
            self.hide()
        else:
            self._ajuster_geometrie()
            self.show()
            self.raise_()

    # --- Geometrie -----------------------------------------------------------

    def _lignes(self) -> list[tuple[str, str, QColor]]:
        return [
            (reading.label, formater_valeur(reading), couleur_valeur(reading))
            for reading in self._readings
        ]

    def _dimensions(self) -> tuple[int, int, int, int, int]:
        """(largeur, hauteur, largeur libelle, largeur valeur, lignes par colonne)."""
        lignes = self._lignes()
        if not lignes:
            return 0, 0, 0, 0, 0

        largeur_libelle = max(self._metrics.horizontalAdvance(label) for label, _, _ in lignes)
        largeur_valeur = max(self._metrics.horizontalAdvance(valeur) for _, valeur, _ in lignes)
        hauteur_ligne = self._metrics.height() + 4

        colonnes = max(1, min(self.config.columns, len(lignes)))
        par_colonne = -(-len(lignes) // colonnes)  # division entiere arrondie au superieur
        largeur_colonne = largeur_libelle + 16 + largeur_valeur

        largeur = MARGE_INTERNE * 2 + colonnes * largeur_colonne + (colonnes - 1) * ESPACE_COLONNES
        hauteur = MARGE_INTERNE * 2 + par_colonne * hauteur_ligne
        return largeur, hauteur, largeur_libelle, largeur_valeur, par_colonne

    def _ajuster_geometrie(self) -> None:
        largeur, hauteur, *_ = self._dimensions()
        if largeur == 0:
            self.resize(1, 1)
            return

        ecran = self.screen() or QApplication.primaryScreen()
        if ecran is None:  # pragma: no cover - aucun affichage disponible
            self.resize(largeur, hauteur)
            return

        zone = ecran.availableGeometry()
        marge = self.config.margin
        x = zone.left() + marge
        y = zone.top() + marge
        if self.config.position.endswith("right"):
            x = zone.right() - largeur - marge
        if self.config.position.startswith("bottom"):
            y = zone.bottom() - hauteur - marge
        self.setGeometry(x, y, largeur, hauteur)

    # --- Rendu ---------------------------------------------------------------

    def paintEvent(self, event) -> None:  # noqa: N802 - signature imposee par Qt
        lignes = self._lignes()
        if not lignes:
            return

        _, _, largeur_libelle, largeur_valeur, par_colonne = self._dimensions()
        peintre = QPainter(self)
        peintre.setRenderHint(QPainter.RenderHint.Antialiasing, True)
        peintre.setRenderHint(QPainter.RenderHint.TextAntialiasing, True)

        fond = QPainterPath()
        fond.addRoundedRect(self.rect().adjusted(0, 0, -1, -1), 10, 10)
        peintre.fillPath(fond, COULEUR_FOND)

        hauteur_ligne = self._metrics.height() + 4
        largeur_colonne = largeur_libelle + 16 + largeur_valeur
        base = self._metrics.ascent()

        for index, (label, valeur, couleur) in enumerate(lignes):
            colonne, rang = divmod(index, par_colonne)
            x = MARGE_INTERNE + colonne * (largeur_colonne + ESPACE_COLONNES)
            y = MARGE_INTERNE + rang * hauteur_ligne + base

            self._texte(peintre, x, y, label, COULEUR_LIBELLE)
            decalage = largeur_colonne - self._metrics.horizontalAdvance(valeur)
            self._texte(peintre, x + decalage, y, valeur, couleur)

        peintre.end()

    def _texte(self, peintre: QPainter, x: int, y: int, texte: str, couleur: QColor) -> None:
        """Texte cerne de noir : lisible sur un fond de jeu clair comme sombre."""
        chemin = QPainterPath()
        chemin.addText(float(x), float(y), self.font(), texte)
        peintre.setPen(QPen(COULEUR_CONTOUR, 2.4))
        peintre.setBrush(Qt.BrushStyle.NoBrush)
        peintre.drawPath(chemin)
        peintre.setPen(Qt.PenStyle.NoPen)
        peintre.setBrush(couleur)
        peintre.drawPath(chemin)


def creer_minuterie(fenetre: OverlayWindow, source, intervalle_ms: int) -> QTimer:
    """Rafraichit la fenetre depuis `source()` (le dernier snapshot du hub).

    Le hub tourne dans un thread asyncio separe ; on ne lit ici qu'une reference
    vers un `Snapshot` immuable, ce qui evite tout verrou cote interface.
    """
    minuterie = QTimer(fenetre)

    def tick() -> None:
        snapshot = source()
        if snapshot is not None:
            fenetre.appliquer(snapshot)

    minuterie.timeout.connect(tick)
    minuterie.start(max(50, intervalle_ms))
    return minuterie
