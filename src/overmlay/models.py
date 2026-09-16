"""Modele de donnees partage entre les capteurs, le serveur et les clients."""

from __future__ import annotations

import math
import socket
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Any


class Group(str, Enum):
    """Famille de materiel a laquelle se rattache une mesure."""

    CPU = "cpu"
    GPU = "gpu"
    MEMORY = "memory"
    FAN = "fan"
    FPS = "fps"
    STORAGE = "storage"
    NETWORK = "network"
    SYSTEM = "system"


class Kind(str, Enum):
    """Nature physique d'une mesure, utilisee pour le rendu (jauge, couleur, unite)."""

    TEMPERATURE = "temperature"
    LOAD = "load"
    FREQUENCY = "frequency"
    MEMORY = "memory"
    FAN = "fan"
    POWER = "power"
    VOLTAGE = "voltage"
    FPS = "fps"
    DURATION = "duration"
    RATE = "rate"


#: Bornes par defaut utilisees pour dessiner une jauge quand le capteur n'en fournit pas.
DEFAULT_RANGES: dict[Kind, tuple[float, float]] = {
    Kind.TEMPERATURE: (20.0, 100.0),
    Kind.LOAD: (0.0, 100.0),
    Kind.FAN: (0.0, 3000.0),
    Kind.FPS: (0.0, 240.0),
    Kind.DURATION: (0.0, 50.0),
    Kind.FREQUENCY: (0.0, 6000.0),
    Kind.MEMORY: (0.0, 32768.0),
    Kind.POWER: (0.0, 500.0),
    Kind.VOLTAGE: (0.0, 2.0),
    Kind.RATE: (0.0, 100.0),
}

#: Mesures pour lesquelles une jauge a un sens : ailleurs, l'echelle haute est
#: arbitraire et les clients se contentent d'afficher la valeur numerique.
GAUGE_KINDS: frozenset[Kind] = frozenset(
    {Kind.TEMPERATURE, Kind.LOAD, Kind.FAN, Kind.MEMORY, Kind.POWER, Kind.FPS}
)


@dataclass(frozen=True, slots=True)
class Reading:
    """Une mesure unitaire produite par un backend de capteurs.

    `key` est un identifiant stable (`cpu.temp.package`, `gpu.0.fan`) : c'est lui qui
    sert de selecteur dans la configuration de l'overlay et de l'application mobile.
    """

    key: str
    label: str
    value: float | None
    unit: str
    group: Group
    kind: Kind
    minimum: float | None = None
    maximum: float | None = None
    source: str = ""
    extra: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not self.key:
            raise ValueError("Reading.key ne peut pas etre vide")
        if self.value is not None and not math.isfinite(self.value):
            # Un capteur absent renvoie parfois NaN/inf : on le normalise en "pas de valeur".
            object.__setattr__(self, "value", None)

    @property
    def range(self) -> tuple[float, float]:
        """Bornes d'affichage, en completant avec les valeurs par defaut du `kind`."""
        low, high = DEFAULT_RANGES.get(self.kind, (0.0, 100.0))
        return (
            self.minimum if self.minimum is not None else low,
            self.maximum if self.maximum is not None else high,
        )

    def to_dict(self) -> dict[str, Any]:
        low, high = self.range
        payload: dict[str, Any] = {
            "key": self.key,
            "label": self.label,
            "value": self.value,
            "unit": self.unit,
            "group": self.group.value,
            "kind": self.kind.value,
            "min": low,
            "max": high,
            "gauge": self.kind in GAUGE_KINDS,
            "source": self.source,
        }
        if self.extra:
            payload["extra"] = self.extra
        return payload


@dataclass(frozen=True, slots=True)
class Snapshot:
    """Etat complet du systeme a un instant donne."""

    timestamp: float
    host: str
    readings: tuple[Reading, ...]

    @classmethod
    def build(
        cls,
        readings: list[Reading] | tuple[Reading, ...],
        *,
        timestamp: float | None = None,
        host: str | None = None,
    ) -> Snapshot:
        return cls(
            timestamp=time.time() if timestamp is None else timestamp,
            host=host or socket.gethostname(),
            readings=tuple(readings),
        )

    def get(self, key: str) -> Reading | None:
        for reading in self.readings:
            if reading.key == key:
                return reading
        return None

    def by_group(self, group: Group) -> tuple[Reading, ...]:
        return tuple(r for r in self.readings if r.group is group)

    def filter(self, keys: list[str] | tuple[str, ...] | None) -> Snapshot:
        """Restreint le snapshot a une liste de cles (ordre de `keys` conserve).

        Un element de `keys` terminant par `*` agit comme un prefixe (`gpu.*`).
        `None` ou une liste vide renvoie le snapshot inchange.
        """
        if not keys:
            return self
        selected: list[Reading] = []
        seen: set[str] = set()
        for pattern in keys:
            for reading in self.readings:
                if reading.key in seen:
                    continue
                matched = (
                    reading.key.startswith(pattern[:-1])
                    if pattern.endswith("*")
                    else reading.key == pattern
                )
                if matched:
                    selected.append(reading)
                    seen.add(reading.key)
        return Snapshot(timestamp=self.timestamp, host=self.host, readings=tuple(selected))

    def to_dict(self) -> dict[str, Any]:
        return {
            "timestamp": self.timestamp,
            "host": self.host,
            "readings": [r.to_dict() for r in self.readings],
        }
