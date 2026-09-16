"""Contrat commun a tous les backends de capteurs."""

from __future__ import annotations

import abc
import logging
from typing import Any

from overlay.models import Reading

log = logging.getLogger(__name__)


class SensorBackend(abc.ABC):
    """Source de mesures materiel.

    Un backend est volontairement synchrone et bloquant : le hub l'execute dans un
    thread pour ne pas bloquer la boucle asyncio du serveur.
    """

    #: Identifiant court, utilise dans les logs et dans `Reading.source`.
    name: str = "backend"
    #: Libelle lisible affiche par `overlay sensors`.
    description: str = ""
    #: Cadence de rappel dans les journaux quand un backend echoue en boucle.
    _LOG_EVERY: int = 60

    @property
    def _failures(self) -> int:
        # Attribut paresseux : les sous-classes ne sont pas tenues d'appeler super().__init__.
        return getattr(self, "_failure_count", 0)

    @_failures.setter
    def _failures(self, value: int) -> None:
        self._failure_count = value

    def available(self) -> bool:
        """Indique si le backend peut fonctionner sur cette machine."""
        return True

    @abc.abstractmethod
    def read(self) -> list[Reading]:
        """Retourne les mesures courantes. Peut lever : le hub isole les erreurs."""

    def close(self) -> None:  # noqa: B027 - surcharge facultative, sans ressource a liberer
        """Libere les ressources (handles, process externes)."""

    def safe_read(self) -> list[Reading]:
        """`read()` protege : une panne de capteur ne doit jamais tuer la boucle.

        Une sonde durablement HS est interrogee a chaque cycle : la trace complete
        n'est journalisee qu'a la premiere erreur, puis une ligne de rappel toutes
        les `_LOG_EVERY` occurrences, pour ne pas noyer les journaux a 1 Hz.
        """
        try:
            readings = self.read()
        except Exception:
            self._failures += 1
            if self._failures == 1:
                log.warning("Backend %s : lecture en echec", self.name, exc_info=True)
            elif self._failures % self._LOG_EVERY == 0:
                log.warning("Backend %s : %d echecs consecutifs", self.name, self._failures)
            return []
        if self._failures:
            log.info("Backend %s : lecture retablie apres %d echecs", self.name, self._failures)
            self._failures = 0
        return readings

    def describe(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "description": self.description,
            "available": self.available(),
        }


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def signaler(
    logger: logging.Logger,
    warnings: list[str] | None,
    message: str,
    *,
    niveau: int = logging.INFO,
) -> None:
    """Route un message vers la liste structuree si fournie, sinon vers les journaux.

    Utilise par les modules de detection (capteurs, source FPS) : `warnings`, quand
    la CLI le fournit, recueille un diagnostic actionnable qu'elle affiche clairement
    au demarrage plutot que de laisser une mesure manquer sans explication. Un
    appelant qui ne le fournit pas garde au moins la trace dans ses propres journaux.
    """
    if warnings is not None:
        warnings.append(message)
    else:
        logger.log(niveau, message)


def to_float(raw: Any) -> float | None:
    """Convertit une valeur brute de capteur en float, ou `None` si illisible.

    Les sondes renvoient regulierement `N/A`, `[Not Supported]` ou une chaine vide.
    """
    if raw is None:
        return None
    if isinstance(raw, bool):
        return float(raw)
    if isinstance(raw, (int, float)):
        return float(raw)
    text = str(raw).strip()
    if not text:
        return None
    # LibreHardwareMonitor formate ses valeurs selon la locale Windows : "62,3 °C" en
    # francais, "1,234.5" en anglais. Quand les deux separateurs sont presents, la
    # virgule separe les milliers ; seule, elle est le separateur decimal.
    if "," in text:
        text = text.replace(",", "") if "." in text else text.replace(",", ".")
    # Retire une eventuelle unite collee a la valeur ("53.0 C", "1200RPM").
    cleaned = []
    for char in text:
        if char.isdigit() or char in "+-.":
            cleaned.append(char)
        elif cleaned:
            break
    candidate = "".join(cleaned)
    if candidate in ("", "+", "-", ".", "+.", "-."):
        return None
    try:
        return float(candidate)
    except ValueError:
        return None
