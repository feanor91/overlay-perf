"""Lecture directe de /sys/class/hwmon (Linux).

Plus complet que psutil : en plus des temperatures et des vitesses de rotation, on
recupere le rapport cyclique PWM des ventilateurs et la consommation des rails
exposes par la carte mere.
"""

from __future__ import annotations

import re
from pathlib import Path

from overmlay.models import Group, Kind, Reading
from overmlay.sensors.base import SensorBackend

HWMON_ROOT = Path("/sys/class/hwmon")

_TEMP_RE = re.compile(r"^temp(\d+)_input$")
_FAN_RE = re.compile(r"^fan(\d+)_input$")
_PWM_RE = re.compile(r"^pwm(\d+)$")
_POWER_RE = re.compile(r"^power(\d+)_average$")

#: Puces dont les mesures relevent du GPU plutot que de la carte mere.
_GPU_CHIPS = ("amdgpu", "nouveau", "radeon", "nvidia", "i915", "xe")
_CPU_CHIPS = ("coretemp", "k10temp", "zenpower", "cpu_thermal", "k8temp")


def _slug(text: str) -> str:
    out = [c.lower() if c.isalnum() else "_" for c in text.strip()]
    return "".join(out).strip("_") or "unknown"


def _read_text(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8", errors="replace").strip()
    except (OSError, UnicodeDecodeError):
        return None


def _read_number(path: Path) -> float | None:
    raw = _read_text(path)
    if raw is None:
        return None
    try:
        return float(raw)
    except ValueError:
        return None


class LinuxHwmonBackend(SensorBackend):
    name = "hwmon"
    description = "Sondes materielles Linux (/sys/class/hwmon) : temperatures, ventilateurs, PWM"

    def __init__(
        self,
        root: Path | str = HWMON_ROOT,
        *,
        exclude: set[Path] | frozenset[Path] | None = None,
    ) -> None:
        self.root = Path(root)
        # Peripheriques deja couverts par un backend dedie (le sous-repertoire
        # hwmon d'un GPU AMD, par exemple) : les relire ici publierait la meme
        # sonde une seconde fois sous un autre nom.
        self.exclude = {Path(p) for p in (exclude or ())}

    def available(self) -> bool:
        return self.root.is_dir() and any(self.root.iterdir())

    def _excluded(self, device: Path) -> bool:
        if not self.exclude:
            return False
        try:
            resolved = device.resolve()
        except OSError:  # pragma: no cover - lien symbolique casse
            return False
        return resolved in self.exclude

    def read(self) -> list[Reading]:
        readings: list[Reading] = []
        try:
            devices = sorted(self.root.iterdir())
        except OSError:
            return []
        for device in devices:
            if self._excluded(device):
                continue
            readings.extend(self._read_device(device))
        return readings

    # --- Interne -------------------------------------------------------------

    def _read_device(self, device: Path) -> list[Reading]:
        chip = _read_text(device / "name") or device.name
        prefix = _slug(chip)
        group = self._chip_group(chip)
        readings: list[Reading] = []

        try:
            entries = sorted(p.name for p in device.iterdir())
        except OSError:
            return []

        for entry in entries:
            if match := _TEMP_RE.match(entry):
                reading = self._temperature(device, prefix, chip, group, match.group(1))
            elif match := _FAN_RE.match(entry):
                reading = self._fan(device, prefix, chip, match.group(1))
            elif match := _PWM_RE.match(entry):
                reading = self._pwm(device, prefix, chip, match.group(1))
            elif match := _POWER_RE.match(entry):
                reading = self._power(device, prefix, chip, group, match.group(1))
            else:
                continue
            if reading is not None:
                readings.append(reading)
        return readings

    def _temperature(
        self, device: Path, prefix: str, chip: str, group: Group, index: str
    ) -> Reading | None:
        raw = _read_number(device / f"temp{index}_input")
        if raw is None:
            return None
        label = _read_text(device / f"temp{index}_label") or f"{chip} temp{index}"
        critical = _read_number(device / f"temp{index}_crit")
        maximum = critical / 1000.0 if critical else 100.0
        return Reading(
            key=f"temp.{prefix}.{_slug(label)}",
            label=label,
            value=round(raw / 1000.0, 1),
            unit="°C",
            group=group,
            kind=Kind.TEMPERATURE,
            maximum=maximum,
            source=self.name,
            extra={"chip": chip},
        )

    def _fan(self, device: Path, prefix: str, chip: str, index: str) -> Reading | None:
        rpm = _read_number(device / f"fan{index}_input")
        if rpm is None:
            return None
        label = _read_text(device / f"fan{index}_label") or f"{chip} ventilateur {index}"
        maximum = _read_number(device / f"fan{index}_max")
        return Reading(
            key=f"fan.{prefix}.{index}.rpm",
            label=label,
            value=rpm,
            unit="RPM",
            group=Group.FAN,
            kind=Kind.FAN,
            minimum=0.0,
            maximum=maximum or 3000.0,
            source=self.name,
            extra={"chip": chip},
        )

    def _pwm(self, device: Path, prefix: str, chip: str, index: str) -> Reading | None:
        raw = _read_number(device / f"pwm{index}")
        if raw is None:
            return None
        return Reading(
            key=f"fan.{prefix}.{index}.pwm",
            label=f"{chip} PWM {index}",
            value=round(raw / 255.0 * 100.0, 1),
            unit="%",
            group=Group.FAN,
            kind=Kind.LOAD,
            source=self.name,
            extra={"chip": chip},
        )

    def _power(
        self, device: Path, prefix: str, chip: str, group: Group, index: str
    ) -> Reading | None:
        micro_watts = _read_number(device / f"power{index}_average")
        if micro_watts is None:
            return None
        cap = _read_number(device / f"power{index}_cap")
        return Reading(
            key=f"power.{prefix}.{index}",
            label=f"{chip} puissance {index}",
            value=round(micro_watts / 1_000_000.0, 1),
            unit="W",
            group=group,
            kind=Kind.POWER,
            minimum=0.0,
            maximum=round(cap / 1_000_000.0, 1) if cap else 250.0,
            source=self.name,
            extra={"chip": chip},
        )

    @staticmethod
    def _chip_group(chip: str) -> Group:
        lowered = chip.lower()
        if any(token in lowered for token in _GPU_CHIPS):
            return Group.GPU
        if any(token in lowered for token in _CPU_CHIPS):
            return Group.CPU
        if "nvme" in lowered or "drivetemp" in lowered:
            return Group.STORAGE
        return Group.SYSTEM
