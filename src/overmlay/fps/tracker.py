"""Calcul des metriques d'images par seconde a partir d'horodatages de trames.

Le suivi est independant de la source : PresentMon, MangoHud ou un jeu qui pousse
ses trames sur l'API HTTP alimentent tous le meme tracker.
"""

from __future__ import annotations

import threading
import time
from collections import deque
from dataclasses import dataclass

from overmlay.models import Group, Kind, Reading
from overmlay.sensors.base import SensorBackend


@dataclass(frozen=True, slots=True)
class FpsStats:
    """Instantane des metriques de fluidite."""

    fps: float | None
    frame_time_ms: float | None
    low_1_percent: float | None
    low_01_percent: float | None
    frame_count: int
    application: str | None = None
    stale: bool = False


def percentile(sorted_values: list[float], fraction: float) -> float:
    """Percentile par interpolation lineaire sur une liste deja triee.

    `fraction` est dans [0, 1] : 0.99 renvoie le 99e percentile.
    """
    if not sorted_values:
        raise ValueError("liste vide")
    if len(sorted_values) == 1:
        return sorted_values[0]
    position = fraction * (len(sorted_values) - 1)
    low = int(position)
    high = min(low + 1, len(sorted_values) - 1)
    weight = position - low
    return sorted_values[low] * (1 - weight) + sorted_values[high] * weight


class FrameTimeTracker:
    """Fenetre glissante d'intervalles entre trames, alimentee par plusieurs threads.

    `window_seconds` regle la moyenne affichee (1 s donne un compteur reactif mais
    stable) ; `capacity` borne l'historique servant aux centiles bas.
    """

    def __init__(
        self,
        *,
        window_seconds: float = 1.0,
        capacity: int = 2000,
        stale_after: float = 2.0,
        max_frame_time_ms: float = 1000.0,
    ) -> None:
        if window_seconds <= 0:
            raise ValueError("window_seconds doit etre strictement positif")
        self.window_seconds = window_seconds
        self.stale_after = stale_after
        self.max_frame_time_ms = max_frame_time_ms
        self._lock = threading.Lock()
        # Chaque entree est (instant de reception, duree de trame) : la date de
        # reception sert a la fenetre glissante, la duree vient de la source. Les
        # deux sont distincts car PresentMon fournit des horodatages sur sa propre
        # base de temps, sans rapport avec l'horloge monotone locale.
        self._frames: deque[tuple[float, float]] = deque(maxlen=capacity)
        self._last_source_timestamp: float | None = None
        self._last_arrival: float | None = None
        self._application: str | None = None

    # --- Alimentation --------------------------------------------------------

    def add_frame(self, timestamp: float | None = None, *, application: str | None = None) -> None:
        """Enregistre la presentation d'une trame datee de `timestamp`, en secondes.

        L'horodatage ne sert qu'a calculer l'ecart avec la trame precedente : il peut
        donc venir de n'importe quelle base de temps croissante.
        """
        arrival = time.monotonic()
        source = arrival if timestamp is None else timestamp
        with self._lock:
            if application is not None:
                self._application = application
            previous = self._last_source_timestamp
            self._last_source_timestamp = source
            self._last_arrival = arrival
            if previous is None:
                return
            delta_ms = (source - previous) * 1000.0
            # Ecart nul ou negatif : trame dupliquee, ou horloge qui recule. On
            # repart de cette trame comme nouvelle reference sans rien enregistrer.
            if delta_ms <= 0:
                return
            self._record(arrival, delta_ms)

    def add_frame_time(self, frame_time_ms: float, *, application: str | None = None) -> None:
        """Enregistre directement une duree de trame en millisecondes."""
        if frame_time_ms <= 0:
            return
        arrival = time.monotonic()
        with self._lock:
            if application is not None:
                self._application = application
            self._last_arrival = arrival
            self._record(arrival, frame_time_ms)

    def _record(self, arrival: float, frame_time_ms: float) -> None:
        """Ajoute un echantillon en ecartant les discontinuites de flux.

        Une trame de plus d'une seconde n'est pas une trame lente mais une coupure
        (alt-tab, ecran de chargement, jeu quitte) : la conserver fausserait
        durablement les centiles bas.
        """
        if frame_time_ms > self.max_frame_time_ms:
            return
        self._frames.append((arrival, frame_time_ms))

    def reset(self) -> None:
        with self._lock:
            self._frames.clear()
            self._last_source_timestamp = None
            self._last_arrival = None
            self._application = None

    # --- Lecture -------------------------------------------------------------

    def stats(self) -> FpsStats:
        now = time.monotonic()
        with self._lock:
            application = self._application
            last = self._last_arrival
            recent = [ms for ts, ms in self._frames if now - ts <= self.window_seconds]
            history = [ms for _, ms in self._frames]

        stale = last is None or (now - last) > self.stale_after
        if stale or not recent:
            return FpsStats(
                fps=None,
                frame_time_ms=None,
                low_1_percent=None,
                low_01_percent=None,
                frame_count=len(history),
                application=application,
                stale=True,
            )

        mean_frame_time = sum(recent) / len(recent)
        ordered = sorted(history)
        # Un "1 % low" est l'inverse du 99e centile de duree de trame : il decrit les
        # trames les plus lentes, celles que l'oeil percoit comme des saccades.
        low_1 = 1000.0 / percentile(ordered, 0.99) if len(ordered) >= 20 else None
        low_01 = 1000.0 / percentile(ordered, 0.999) if len(ordered) >= 200 else None
        return FpsStats(
            fps=round(1000.0 / mean_frame_time, 1),
            frame_time_ms=round(mean_frame_time, 2),
            low_1_percent=round(low_1, 1) if low_1 else None,
            low_01_percent=round(low_01, 1) if low_01 else None,
            frame_count=len(history),
            application=application,
            stale=False,
        )


class FpsBackend(SensorBackend):
    """Expose les metriques du tracker sous forme de mesures standard."""

    name = "fps"
    description = "Images par seconde et temps de trame (PresentMon, MangoHud ou API)"

    def __init__(self, tracker: FrameTimeTracker) -> None:
        self.tracker = tracker

    def read(self) -> list[Reading]:
        stats = self.tracker.stats()
        extra = {"application": stats.application} if stats.application else {}
        specs = [
            ("fps.current", "FPS", stats.fps, "FPS", Kind.FPS, 0.0, None),
            ("fps.frametime", "Temps de trame", stats.frame_time_ms, "ms",
             Kind.DURATION, 0.0, 50.0),
            ("fps.low1", "1 % low", stats.low_1_percent, "FPS", Kind.FPS, 0.0, None),
            ("fps.low01", "0,1 % low", stats.low_01_percent, "FPS", Kind.FPS, 0.0, None),
        ]
        return [
            Reading(
                key=key,
                label=label,
                value=value,
                unit=unit,
                group=Group.FPS,
                kind=kind,
                minimum=low,
                maximum=high,
                source=self.name,
                extra=extra,
            )
            for key, label, value, unit, kind, low, high in specs
        ]
