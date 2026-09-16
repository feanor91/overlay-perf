"""Fixtures partagees."""

from __future__ import annotations

import pytest

from overlay.fps.tracker import FrameTimeTracker
from overlay.hub import MetricsHub
from overlay.sensors.mock import MockBackend


@pytest.fixture
def tracker() -> FrameTimeTracker:
    return FrameTimeTracker(window_seconds=5.0, stale_after=60.0)


@pytest.fixture
def mock_hub() -> MetricsHub:
    return MetricsHub([MockBackend(seed=1234)], poll_interval=0.05, history_size=10)
