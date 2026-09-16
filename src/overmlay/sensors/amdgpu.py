"""Capteurs GPU AMD sous Linux, via l'interface sysfs du pilote amdgpu.

Les temperatures et les ventilateurs de ces cartes sont deja exposes par hwmon :
ce backend ne couvre que ce que hwmon ne fournit pas (occupation GPU et VRAM).
"""

from __future__ import annotations

from pathlib import Path

from overmlay.models import Group, Kind, Reading
from overmlay.sensors.base import SensorBackend

DRM_ROOT = Path("/sys/class/drm")
_MIB = 1024 * 1024


def _read_number(path: Path) -> float | None:
    try:
        return float(path.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return None


def _read_name(device: Path) -> str | None:
    for candidate in ("product_name", "device"):
        path = device / candidate
        try:
            value = path.read_text(encoding="utf-8").strip()
        except OSError:
            continue
        if value:
            return value
    return None


class AmdGpuBackend(SensorBackend):
    name = "amdgpu"
    description = "GPU AMD sous Linux (sysfs amdgpu) : occupation GPU et VRAM"

    def __init__(self, root: Path | str = DRM_ROOT) -> None:
        self.root = Path(root)

    def _cards(self) -> list[Path]:
        if not self.root.is_dir():
            return []
        cards = []
        try:
            entries = sorted(self.root.iterdir())
        except OSError:
            return []
        for entry in entries:
            # `card0` oui, `card0-DP-1` (connecteur d'affichage) non.
            if not entry.name.startswith("card") or "-" in entry.name:
                continue
            device = entry / "device"
            if (device / "gpu_busy_percent").exists():
                cards.append(device)
        return cards

    def available(self) -> bool:
        return bool(self._cards())

    def read(self) -> list[Reading]:
        readings: list[Reading] = []
        for index, device in enumerate(self._cards()):
            name = _read_name(device) or f"GPU AMD {index}"
            extra = {"device": name}
            busy = _read_number(device / "gpu_busy_percent")
            if busy is not None:
                readings.append(
                    Reading(
                        key=f"gpu.{index}.load",
                        label=f"GPU {index} charge",
                        value=busy,
                        unit="%",
                        group=Group.GPU,
                        kind=Kind.LOAD,
                        minimum=0.0,
                        maximum=100.0,
                        source=self.name,
                        extra=extra,
                    )
                )
            mem_busy = _read_number(device / "mem_busy_percent")
            if mem_busy is not None:
                readings.append(
                    Reading(
                        key=f"gpu.{index}.vram.load",
                        label=f"GPU {index} bus memoire",
                        value=mem_busy,
                        unit="%",
                        group=Group.GPU,
                        kind=Kind.LOAD,
                        minimum=0.0,
                        maximum=100.0,
                        source=self.name,
                        extra=extra,
                    )
                )
            used = _read_number(device / "mem_info_vram_used")
            total = _read_number(device / "mem_info_vram_total")
            if used is not None:
                readings.append(
                    Reading(
                        key=f"gpu.{index}.vram.used",
                        label=f"GPU {index} VRAM",
                        value=round(used / _MIB, 1),
                        unit="MiB",
                        group=Group.GPU,
                        kind=Kind.MEMORY,
                        minimum=0.0,
                        maximum=round(total / _MIB, 1) if total else None,
                        source=self.name,
                        extra=extra,
                    )
                )
        return readings
