"""Hub de collecte : interroge les backends et diffuse les snapshots aux abonnes."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
from collections import deque
from collections.abc import Iterable

from overlay.models import Reading, Snapshot
from overlay.sensors.base import SensorBackend

log = logging.getLogger(__name__)


def merge_readings(groups: Iterable[list[Reading]]) -> list[Reading]:
    """Fusionne les mesures de plusieurs backends en dedoublonnant par cle.

    L'ordre des backends porte la priorite : le premier a publier une cle gagne.
    C'est ce qui permet a NVML de primer sur hwmon pour `gpu.0.temp`.
    """
    merged: list[Reading] = []
    seen: set[str] = set()
    for readings in groups:
        for reading in readings:
            if reading.key in seen:
                continue
            seen.add(reading.key)
            merged.append(reading)
    return merged


class MetricsHub:
    """Boucle d'echantillonnage partagee par l'overlay et le serveur.

    Les backends sont synchrones et bloquants : ils sont executes dans un thread
    afin que la boucle asyncio du serveur reste disponible pendant les lectures.
    """

    def __init__(
        self,
        backends: list[SensorBackend],
        *,
        poll_interval: float = 1.0,
        history_size: int = 300,
    ) -> None:
        if poll_interval <= 0:
            raise ValueError("poll_interval doit etre strictement positif")
        self.backends = backends
        self.poll_interval = poll_interval
        self._history: deque[Snapshot] = deque(maxlen=max(1, history_size))
        self._subscribers: set[asyncio.Queue[Snapshot]] = set()
        self._task: asyncio.Task[None] | None = None
        self._stopping = asyncio.Event()

    # --- Cycle de vie --------------------------------------------------------

    async def start(self) -> None:
        if self._task is not None:
            return
        self._stopping.clear()
        self._task = asyncio.create_task(self._loop(), name="overlay-hub")

    async def stop(self) -> None:
        self._stopping.set()
        task = self._task
        self._task = None
        if task is not None:
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task
        for backend in self.backends:
            try:
                backend.close()
            except Exception:  # pragma: no cover - fermeture best-effort
                log.debug("Fermeture du backend %s en echec", backend.name, exc_info=True)

    async def __aenter__(self) -> MetricsHub:
        await self.start()
        return self

    async def __aexit__(self, *_exc: object) -> None:
        await self.stop()

    # --- Collecte ------------------------------------------------------------

    def poll_sync(self) -> Snapshot:
        """Interroge tous les backends dans le thread courant."""
        snapshot = Snapshot.build(merge_readings(b.safe_read() for b in self.backends))
        self._history.append(snapshot)
        return snapshot

    async def poll(self) -> Snapshot:
        snapshot = await asyncio.to_thread(
            lambda: Snapshot.build(merge_readings(b.safe_read() for b in self.backends))
        )
        self._history.append(snapshot)
        self._publish(snapshot)
        return snapshot

    async def _loop(self) -> None:
        while not self._stopping.is_set():
            started = time.monotonic()
            try:
                await self.poll()
            except asyncio.CancelledError:
                raise
            except Exception:
                log.exception("Cycle de collecte en echec")
            # On soustrait la duree de lecture pour garder une cadence reguliere
            # meme quand un capteur lent (nvidia-smi) prend plusieurs centaines de ms.
            delay = max(0.0, self.poll_interval - (time.monotonic() - started))
            try:
                await asyncio.wait_for(self._stopping.wait(), timeout=delay)
            except asyncio.TimeoutError:
                continue

    # --- Diffusion -----------------------------------------------------------

    def subscribe(self, maxsize: int = 4) -> asyncio.Queue[Snapshot]:
        queue: asyncio.Queue[Snapshot] = asyncio.Queue(maxsize=maxsize)
        self._subscribers.add(queue)
        return queue

    def unsubscribe(self, queue: asyncio.Queue[Snapshot]) -> None:
        self._subscribers.discard(queue)

    def _publish(self, snapshot: Snapshot) -> None:
        for queue in list(self._subscribers):
            try:
                queue.put_nowait(snapshot)
            except asyncio.QueueFull:
                # Client trop lent (telephone sur un Wi-Fi charge) : on jette la
                # mesure la plus ancienne, un etat temps reel perime n'a pas d'interet.
                try:
                    queue.get_nowait()
                    queue.put_nowait(snapshot)
                except (asyncio.QueueEmpty, asyncio.QueueFull):  # pragma: no cover
                    pass

    # --- Lecture -------------------------------------------------------------

    @property
    def latest(self) -> Snapshot | None:
        return self._history[-1] if self._history else None

    @property
    def subscriber_count(self) -> int:
        return len(self._subscribers)

    def history(self, limit: int | None = None) -> list[Snapshot]:
        snapshots = list(self._history)
        return snapshots[-limit:] if limit else snapshots

    def describe_backends(self) -> list[dict[str, object]]:
        return [backend.describe() for backend in self.backends]
