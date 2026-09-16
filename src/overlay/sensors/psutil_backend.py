"""Backend portable base sur psutil : charge CPU, memoire, disques, reseau.

Sur Linux, psutil expose egalement les sondes de temperature et les ventilateurs ;
sur Windows ces deux API renvoient un dictionnaire vide et c'est le backend
LibreHardwareMonitor qui prend le relais.
"""

from __future__ import annotations

import time

import psutil

from overlay.models import Group, Kind, Reading
from overlay.sensors.base import SensorBackend

_MIB = 1024 * 1024


def _slug(text: str) -> str:
    """Normalise un nom de sonde en fragment de cle stable."""
    out = [c.lower() if (c.isalnum() or c == ".") else "_" for c in text.strip()]
    return "".join(out).strip("_") or "unknown"


class PsutilBackend(SensorBackend):
    name = "psutil"
    description = "CPU, memoire, disque, reseau (+ temperatures/ventilateurs sous Linux)"

    def __init__(
        self,
        *,
        per_core: bool = False,
        include_io: bool = True,
        include_thermals: bool = True,
    ) -> None:
        self.per_core = per_core
        self.include_io = include_io
        self.include_thermals = include_thermals
        self._last_time: float | None = None
        self._last_net: tuple[int, int] | None = None
        self._last_disk: tuple[int, int] | None = None
        # Premier appel : amorce le compteur interne de psutil, qui renvoie 0.0 sinon.
        psutil.cpu_percent(interval=None)

    def read(self) -> list[Reading]:
        readings: list[Reading] = []
        readings.extend(self._read_cpu())
        readings.extend(self._read_memory())
        if self.include_thermals:
            readings.extend(self._read_temperatures())
            readings.extend(self._read_fans())
        if self.include_io:
            readings.extend(self._read_io())
        return readings

    # --- CPU -----------------------------------------------------------------

    def _read_cpu(self) -> list[Reading]:
        readings = [
            Reading(
                key="cpu.load",
                label="CPU",
                value=psutil.cpu_percent(interval=None),
                unit="%",
                group=Group.CPU,
                kind=Kind.LOAD,
                source=self.name,
            )
        ]
        if self.per_core:
            for index, load in enumerate(psutil.cpu_percent(interval=None, percpu=True)):
                readings.append(
                    Reading(
                        key=f"cpu.core.{index}.load",
                        label=f"Coeur {index}",
                        value=load,
                        unit="%",
                        group=Group.CPU,
                        kind=Kind.LOAD,
                        source=self.name,
                    )
                )
        try:
            freq = psutil.cpu_freq()
        except (NotImplementedError, OSError):
            freq = None
        if freq is not None and freq.current:
            readings.append(
                Reading(
                    key="cpu.clock",
                    label="Frequence CPU",
                    value=round(freq.current, 1),
                    unit="MHz",
                    group=Group.CPU,
                    kind=Kind.FREQUENCY,
                    minimum=freq.min or 0.0,
                    maximum=freq.max or max(freq.current * 1.2, 1.0),
                    source=self.name,
                )
            )
        return readings

    # --- Memoire -------------------------------------------------------------

    def _read_memory(self) -> list[Reading]:
        virtual = psutil.virtual_memory()
        swap = psutil.swap_memory()
        total_mib = virtual.total / _MIB
        return [
            Reading(
                key="memory.load",
                label="RAM",
                value=virtual.percent,
                unit="%",
                group=Group.MEMORY,
                kind=Kind.LOAD,
                source=self.name,
            ),
            Reading(
                key="memory.used",
                label="RAM utilisee",
                value=round(virtual.used / _MIB, 1),
                unit="MiB",
                group=Group.MEMORY,
                kind=Kind.MEMORY,
                minimum=0.0,
                maximum=round(total_mib, 1),
                source=self.name,
            ),
            Reading(
                key="memory.total",
                label="RAM totale",
                value=round(total_mib, 1),
                unit="MiB",
                group=Group.MEMORY,
                kind=Kind.MEMORY,
                minimum=0.0,
                maximum=round(total_mib, 1),
                source=self.name,
            ),
            Reading(
                key="memory.swap",
                label="Swap",
                value=swap.percent,
                unit="%",
                group=Group.MEMORY,
                kind=Kind.LOAD,
                source=self.name,
            ),
        ]

    # --- Thermique -----------------------------------------------------------

    def _read_temperatures(self) -> list[Reading]:
        getter = getattr(psutil, "sensors_temperatures", None)
        if getter is None:
            return []
        try:
            groups = getter()
        except (AttributeError, OSError, NotImplementedError):
            return []

        readings: list[Reading] = []
        for chip, entries in (groups or {}).items():
            for index, entry in enumerate(entries):
                if entry.current is None:
                    continue
                label = entry.label or f"{chip} #{index}"
                readings.append(
                    Reading(
                        key=f"temp.{_slug(chip)}.{_slug(label)}",
                        label=label,
                        value=round(float(entry.current), 1),
                        unit="°C",
                        group=self._guess_group(chip, label),
                        kind=Kind.TEMPERATURE,
                        maximum=float(entry.critical or entry.high or 100.0),
                        source=self.name,
                        extra={"chip": chip},
                    )
                )
        return readings

    def _read_fans(self) -> list[Reading]:
        getter = getattr(psutil, "sensors_fans", None)
        if getter is None:
            return []
        try:
            groups = getter()
        except (AttributeError, OSError, NotImplementedError):
            return []

        readings: list[Reading] = []
        for chip, entries in (groups or {}).items():
            for index, entry in enumerate(entries):
                if entry.current is None:
                    continue
                label = entry.label or f"Ventilateur {index}"
                readings.append(
                    Reading(
                        key=f"fan.{_slug(chip)}.{_slug(label)}",
                        label=label,
                        value=float(entry.current),
                        unit="RPM",
                        group=Group.FAN,
                        kind=Kind.FAN,
                        source=self.name,
                        extra={"chip": chip},
                    )
                )
        return readings

    @staticmethod
    def _guess_group(chip: str, label: str) -> Group:
        haystack = f"{chip} {label}".lower()
        if any(token in haystack for token in ("gpu", "amdgpu", "nouveau", "radeon")):
            return Group.GPU
        if any(token in haystack for token in ("nvme", "drive", "disk", "sd")):
            return Group.STORAGE
        if any(token in haystack for token in ("core", "package", "tctl", "tdie", "cpu", "k10")):
            return Group.CPU
        return Group.SYSTEM

    # --- Entrees/sorties -----------------------------------------------------

    def _read_io(self) -> list[Reading]:
        """Debits disque et reseau, deduits de la variation des compteurs cumulatifs."""
        now = time.monotonic()
        try:
            net = psutil.net_io_counters()
            disk = psutil.disk_io_counters()
        except (OSError, RuntimeError):
            return []

        net_now = (net.bytes_recv, net.bytes_sent) if net else None
        disk_now = (disk.read_bytes, disk.write_bytes) if disk else None

        if self._last_time is None:
            # Premier passage : on pose la reference, aucun debit calculable.
            self._last_time, self._last_net, self._last_disk = now, net_now, disk_now
            return []

        elapsed = now - self._last_time
        if elapsed <= 0:
            # Deux relevés tombes sur le meme tic d'horloge. Sous Windows, avant
            # Python 3.13, time.monotonic() avance par pas d'environ 15 ms :
            # avancer la reference ici ferait disparaitre les debits a repetition.
            # On la conserve pour mesurer sur la fenetre du cycle suivant.
            return []

        readings: list[Reading] = []
        if net_now and self._last_net:
            readings.extend(
                self._rate_pair(
                    "network",
                    ("Reception", "Emission"),
                    net_now,
                    self._last_net,
                    elapsed,
                    Group.NETWORK,
                )
            )
        if disk_now and self._last_disk:
            readings.extend(
                self._rate_pair(
                    "storage",
                    ("Lecture disque", "Ecriture disque"),
                    disk_now,
                    self._last_disk,
                    elapsed,
                    Group.STORAGE,
                )
            )

        self._last_time = now
        self._last_net = net_now
        self._last_disk = disk_now
        return readings

    def _rate_pair(
        self,
        prefix: str,
        labels: tuple[str, str],
        current: tuple[int, int],
        previous: tuple[int, int],
        elapsed: float,
        group: Group,
    ) -> list[Reading]:
        suffixes = ("in", "out") if prefix == "network" else ("read", "write")
        readings = []
        for value_now, value_before, label, suffix in zip(
            current, previous, labels, suffixes, strict=True
        ):
            # Les compteurs cumulatifs peuvent repartir de zero (reset d'interface).
            delta = max(0, value_now - value_before)
            readings.append(
                Reading(
                    key=f"{prefix}.{suffix}",
                    label=label,
                    value=round(delta / elapsed / _MIB, 2),
                    unit="MiB/s",
                    group=group,
                    kind=Kind.RATE,
                    minimum=0.0,
                    maximum=100.0,
                    source=self.name,
                )
            )
        return readings
