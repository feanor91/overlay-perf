"""Outils partages par les tests."""

from __future__ import annotations

from overmlay.models import Group, Kind, Reading
from overmlay.sensors.base import SensorBackend


class StaticBackend(SensorBackend):
    """Backend deterministe : renvoie toujours les memes mesures."""

    name = "static"

    def __init__(self, readings: list[Reading], name: str = "static") -> None:
        self.name = name
        self._readings = readings

    def read(self) -> list[Reading]:
        return list(self._readings)


class BrokenBackend(SensorBackend):
    name = "casse"

    def read(self) -> list[Reading]:
        raise RuntimeError("sonde injoignable")


def reading(key: str, value: float | None = 50.0, **kwargs) -> Reading:
    defaults = {
        "label": key,
        "unit": "%",
        "group": Group.CPU,
        "kind": Kind.LOAD,
    }
    defaults.update(kwargs)
    return Reading(key=key, value=value, **defaults)
