"""Capteurs GPU NVIDIA.

Deux chemins d'acces, du plus rapide au plus portable :
1. NVML via `pynvml` quand la bibliotheque est installee (appel direct, ~1 ms) ;
2. `nvidia-smi` en sous-processus sinon (present avec tout pilote NVIDIA).
"""

from __future__ import annotations

import contextlib
import shutil
import subprocess

from overlay.models import Group, Kind, Reading
from overlay.sensors.base import SensorBackend, to_float

#: Champs demandes a nvidia-smi, dans l'ordre des colonnes CSV renvoyees.
QUERY_FIELDS = (
    "index",
    "name",
    "temperature.gpu",
    "utilization.gpu",
    "utilization.memory",
    "memory.used",
    "memory.total",
    "fan.speed",
    "power.draw",
    "power.limit",
    "clocks.current.graphics",
    "clocks.current.memory",
)


def parse_smi_csv(output: str) -> list[dict[str, float | str | None]]:
    """Transforme la sortie CSV de `nvidia-smi` en dictionnaires par GPU.

    Les champs non supportes par la carte ("[N/A]", "[Not Supported]") deviennent `None`.
    """
    rows: list[dict[str, float | str | None]] = []
    for line in output.splitlines():
        line = line.strip()
        if not line:
            continue
        cells = [cell.strip() for cell in line.split(",")]
        if len(cells) != len(QUERY_FIELDS):
            continue
        row: dict[str, float | str | None] = {}
        for field, cell in zip(QUERY_FIELDS, cells, strict=True):
            row[field] = cell if field == "name" else to_float(cell)
        rows.append(row)
    return rows


class NvidiaBackend(SensorBackend):
    name = "nvidia"
    description = "GPU NVIDIA (NVML ou nvidia-smi) : charge, temperature, VRAM, ventilateur"

    def __init__(self, *, timeout: float = 2.0) -> None:
        self.timeout = timeout
        self._nvml = self._init_nvml()
        self._smi = None if self._nvml else shutil.which("nvidia-smi")

    def available(self) -> bool:
        return self._nvml is not None or self._smi is not None

    def device_count(self) -> int:
        """Nombre de GPU NVIDIA presents.

        Sert a decaler l'index des cartes AMD sur une machine hybride, pour que
        deux GPU ne se disputent pas la cle `gpu.0`.
        """
        if not self.available():
            return 0
        if self._nvml is not None:
            try:
                return int(self._nvml.nvmlDeviceGetCount())
            except Exception:  # pragma: no cover - depend du pilote
                return 0
        return len(self._read_smi())

    def close(self) -> None:
        if self._nvml is not None:
            with contextlib.suppress(Exception):  # pragma: no cover - depend du pilote
                self._nvml.nvmlShutdown()
            self._nvml = None

    def read(self) -> list[Reading]:
        rows = self._read_nvml() if self._nvml is not None else self._read_smi()
        readings: list[Reading] = []
        for row in rows:
            readings.extend(self._to_readings(row))
        return readings

    # --- Sources -------------------------------------------------------------

    @staticmethod
    def _init_nvml():
        try:
            import pynvml
        except ImportError:
            return None
        try:
            pynvml.nvmlInit()
        except Exception:
            return None
        return pynvml

    def _read_nvml(self) -> list[dict[str, float | str | None]]:
        nvml = self._nvml
        assert nvml is not None
        rows: list[dict[str, float | str | None]] = []

        def attempt(func, *args):
            try:
                return func(*args)
            except Exception:
                return None

        for index in range(nvml.nvmlDeviceGetCount()):
            handle = nvml.nvmlDeviceGetHandleByIndex(index)
            name = attempt(nvml.nvmlDeviceGetName, handle)
            if isinstance(name, bytes):
                name = name.decode("utf-8", "replace")
            rates = attempt(nvml.nvmlDeviceGetUtilizationRates, handle)
            memory = attempt(nvml.nvmlDeviceGetMemoryInfo, handle)
            power = attempt(nvml.nvmlDeviceGetPowerUsage, handle)
            limit = attempt(nvml.nvmlDeviceGetEnforcedPowerLimit, handle)
            rows.append(
                {
                    "index": float(index),
                    "name": name or f"GPU {index}",
                    "temperature.gpu": attempt(nvml.nvmlDeviceGetTemperature, handle, 0),
                    "utilization.gpu": float(rates.gpu) if rates else None,
                    "utilization.memory": float(rates.memory) if rates else None,
                    "memory.used": memory.used / (1024 * 1024) if memory else None,
                    "memory.total": memory.total / (1024 * 1024) if memory else None,
                    "fan.speed": attempt(nvml.nvmlDeviceGetFanSpeed, handle),
                    "power.draw": power / 1000.0 if power is not None else None,
                    "power.limit": limit / 1000.0 if limit is not None else None,
                    "clocks.current.graphics": attempt(nvml.nvmlDeviceGetClockInfo, handle, 0),
                    "clocks.current.memory": attempt(nvml.nvmlDeviceGetClockInfo, handle, 2),
                }
            )
        return rows

    def _read_smi(self) -> list[dict[str, float | str | None]]:
        if not self._smi:
            return []
        command = [
            self._smi,
            f"--query-gpu={','.join(QUERY_FIELDS)}",
            "--format=csv,noheader,nounits",
        ]
        try:
            completed = subprocess.run(  # noqa: S603 - binaire resolu via shutil.which
                command,
                capture_output=True,
                text=True,
                timeout=self.timeout,
                check=False,
            )
        except (OSError, subprocess.SubprocessError):
            return []
        if completed.returncode != 0:
            return []
        return parse_smi_csv(completed.stdout)

    # --- Mise en forme -------------------------------------------------------

    def _to_readings(self, row: dict[str, float | str | None]) -> list[Reading]:
        index = int(row.get("index") or 0)
        name = str(row.get("name") or f"GPU {index}")
        vram_total = row.get("memory.total")
        power_limit = row.get("power.limit")
        prefix = f"gpu.{index}"
        common = {"group": Group.GPU, "source": self.name, "extra": {"device": name}}

        specs: list[tuple[str, str, float | None, str, Kind, float | None, float | None]] = [
            (f"{prefix}.load", f"GPU {index} charge", row.get("utilization.gpu"), "%",
             Kind.LOAD, 0.0, 100.0),
            (f"{prefix}.temp", f"GPU {index} temperature", row.get("temperature.gpu"), "°C",
             Kind.TEMPERATURE, None, 95.0),
            (f"{prefix}.fan", f"GPU {index} ventilateur", row.get("fan.speed"), "%",
             Kind.LOAD, 0.0, 100.0),
            (f"{prefix}.vram.used", f"GPU {index} VRAM", row.get("memory.used"), "MiB",
             Kind.MEMORY, 0.0, vram_total),
            (f"{prefix}.vram.load", f"GPU {index} bus memoire", row.get("utilization.memory"), "%",
             Kind.LOAD, 0.0, 100.0),
            (f"{prefix}.power", f"GPU {index} consommation", row.get("power.draw"), "W",
             Kind.POWER, 0.0, power_limit),
            (f"{prefix}.clock.core", f"GPU {index} frequence", row.get("clocks.current.graphics"),
             "MHz", Kind.FREQUENCY, 0.0, None),
            (f"{prefix}.clock.mem", f"GPU {index} frequence VRAM", row.get("clocks.current.memory"),
             "MHz", Kind.FREQUENCY, 0.0, None),
        ]

        readings = []
        for key, label, value, unit, kind, low, high in specs:
            if value is None:
                continue
            readings.append(
                Reading(
                    key=key,
                    label=label,
                    value=round(float(value), 1),
                    unit=unit,
                    kind=kind,
                    minimum=low,
                    maximum=float(high) if high is not None else None,
                    **common,
                )
            )
        return readings
