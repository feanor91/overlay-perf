"""Assemblage des composants : capteurs, suivi FPS et hub, a partir de la configuration."""

from __future__ import annotations

import logging
from dataclasses import dataclass
from pathlib import Path

from overlay.config import Config, resolve_token
from overlay.fps.sources import FrameSource, build_frame_source
from overlay.fps.tracker import FpsBackend, FrameTimeTracker
from overlay.hub import MetricsHub
from overlay.sensors import detect_backends

log = logging.getLogger(__name__)


@dataclass(slots=True)
class Runtime:
    """Composants vivants d'une session Overlay."""

    config: Config
    hub: MetricsHub
    tracker: FrameTimeTracker | None
    frame_source: FrameSource | None
    token: str

    def start_frame_source(self) -> None:
        if self.frame_source is None:
            return
        if self.frame_source.start():
            log.info("Source FPS active : %s", self.frame_source.name)
        else:
            log.info("Source FPS %s indisponible", self.frame_source.name)

    def stop_frame_source(self) -> None:
        if self.frame_source is not None:
            self.frame_source.stop()


def build_runtime(config: Config, *, need_token: bool = True) -> Runtime:
    """Construit le hub et ses sources a partir de la configuration validee."""
    backends = detect_backends(
        per_core=config.sensors.per_core,
        include_io=config.sensors.include_io,
        disabled=frozenset(config.sensors.disabled),
        lhm_url=config.sensors.lhm_url,
        force_mock=config.general.mock,
    )
    log.info("Backends actifs : %s", ", ".join(b.name for b in backends) or "aucun")

    tracker: FrameTimeTracker | None = None
    frame_source: FrameSource | None = None
    if config.fps.mode != "off":
        tracker = FrameTimeTracker(window_seconds=config.fps.window_seconds)
        # Le FPS est place en tete : c'est la mesure la plus regardee, et l'ordre
        # des backends fixe l'ordre d'affichage par defaut.
        backends.insert(0, FpsBackend(tracker))
        frame_source = build_frame_source(
            tracker,
            mode=config.fps.mode,
            presentmon_path=config.fps.presentmon_path or None,
            mangohud_log_dir=Path(config.fps.mangohud_log_dir)
            if config.fps.mangohud_log_dir
            else None,
        )

    hub = MetricsHub(
        backends,
        poll_interval=config.general.poll_interval,
        history_size=config.general.history_size,
    )
    token = resolve_token(config, create=need_token) if need_token else config.server.token
    return Runtime(
        config=config,
        hub=hub,
        tracker=tracker,
        frame_source=frame_source,
        token=token,
    )
