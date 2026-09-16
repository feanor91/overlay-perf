"""Raccourci clavier global pour montrer et masquer l'overlay.

`pynput` est optionnel : sans lui, l'overlay reste pilotable par la CLI et par
l'option `visible_at_start`.
"""

from __future__ import annotations

import contextlib
import logging
from collections.abc import Callable

log = logging.getLogger(__name__)


class HotkeyListener:
    """Ecoute un raccourci global dans un thread dedie.

    La syntaxe est celle de pynput : `<ctrl>+<alt>+o`, `<shift>+<f10>`…
    """

    def __init__(self, combination: str, callback: Callable[[], None]) -> None:
        self.combination = combination
        self.callback = callback
        self._listener = None

    def start(self) -> bool:
        """Active l'ecoute. Retourne False si le raccourci n'a pas pu etre pose."""
        if not self.combination:
            return False
        try:
            from pynput import keyboard
        except ImportError:
            log.info(
                "pynput absent : raccourci global desactive. "
                "Installez-le avec « pip install overmlay[overlay] »."
            )
            return False

        try:
            listener = keyboard.GlobalHotKeys({self.combination: self._on_activate})
            listener.daemon = True
            listener.start()
        except Exception:
            # Wayland, session distante ou permissions d'accessibilite refusees (macOS).
            log.warning(
                "Raccourci global %r indisponible sur cette session.",
                self.combination,
                exc_info=True,
            )
            return False

        self._listener = listener
        log.info("Raccourci global actif : %s", self.combination)
        return True

    def _on_activate(self) -> None:
        try:
            self.callback()
        except Exception:  # pragma: no cover - le thread pynput ne doit pas mourir
            log.exception("Echec du basculement de l'overlay")

    def stop(self) -> None:
        listener = self._listener
        self._listener = None
        if listener is not None:
            with contextlib.suppress(Exception):  # pragma: no cover
                listener.stop()
