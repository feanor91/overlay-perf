"""Backend de demonstration : genere des valeurs plausibles sans materiel.

Utile pour developper l'interface mobile, faire une demo, ou tourner en CI sur une
machine virtuelle depourvue de sondes.
"""

from __future__ import annotations

import math
import random
import time

from overmlay.models import Group, Kind, Reading
from overmlay.sensors.base import SensorBackend


class MockBackend(SensorBackend):
    name = "mock"
    description = "Valeurs simulees (demo et tests, aucun materiel requis)"

    def __init__(self, *, seed: int | None = None, period: float = 30.0) -> None:
        self._random = random.Random(seed)
        self._period = period
        self._start = time.monotonic()

    def _wave(self, base: float, amplitude: float, phase: float) -> float:
        elapsed = time.monotonic() - self._start
        angle = 2 * math.pi * (elapsed / self._period) + phase
        noise = self._random.uniform(-amplitude * 0.08, amplitude * 0.08)
        return base + amplitude * math.sin(angle) + noise

    def read(self) -> list[Reading]:
        cpu_load = max(0.0, min(100.0, self._wave(45, 35, 0.0)))
        gpu_load = max(0.0, min(100.0, self._wave(60, 35, 1.1)))
        specs = [
            ("cpu.load", "CPU", cpu_load, "%", Group.CPU, Kind.LOAD, 0.0, 100.0),
            ("cpu.temp", "CPU temperature", 38 + cpu_load * 0.45, "°C",
             Group.CPU, Kind.TEMPERATURE, None, 95.0),
            ("cpu.clock", "Frequence CPU", self._wave(4200, 500, 0.3), "MHz",
             Group.CPU, Kind.FREQUENCY, 800.0, 5200.0),
            ("gpu.0.load", "GPU charge", gpu_load, "%", Group.GPU, Kind.LOAD, 0.0, 100.0),
            ("gpu.0.temp", "GPU temperature", 40 + gpu_load * 0.4, "°C",
             Group.GPU, Kind.TEMPERATURE, None, 90.0),
            ("gpu.0.fan", "GPU ventilateur", 25 + gpu_load * 0.5, "%",
             Group.GPU, Kind.LOAD, 0.0, 100.0),
            ("gpu.0.power", "GPU puissance", 60 + gpu_load * 1.6, "W",
             Group.GPU, Kind.POWER, 0.0, 250.0),
            ("gpu.0.vram.used", "GPU VRAM", self._wave(6000, 1800, 2.0), "MiB",
             Group.GPU, Kind.MEMORY, 0.0, 12282.0),
            ("memory.load", "RAM", max(0.0, min(100.0, self._wave(52, 12, 2.4))), "%",
             Group.MEMORY, Kind.LOAD, 0.0, 100.0),
            ("fan.cpu", "Ventilateur CPU", self._wave(1200, 350, 0.6), "RPM",
             Group.FAN, Kind.FAN, 0.0, 2200.0),
            ("fan.boitier.1", "Ventilateur boitier 1", self._wave(900, 200, 1.4), "RPM",
             Group.FAN, Kind.FAN, 0.0, 1800.0),
            ("fan.boitier.2", "Ventilateur boitier 2", self._wave(880, 200, 2.8), "RPM",
             Group.FAN, Kind.FAN, 0.0, 1800.0),
        ]
        return [
            Reading(
                key=key,
                label=label,
                value=round(value, 1),
                unit=unit,
                group=group,
                kind=kind,
                minimum=low,
                maximum=high,
                source=self.name,
            )
            for key, label, value, unit, group, kind, low, high in specs
        ]
