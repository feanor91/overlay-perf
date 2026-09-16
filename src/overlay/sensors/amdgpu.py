"""Capteurs GPU AMD sous Linux, via l'interface sysfs du pilote amdgpu.

Publie les memes cles `gpu.N.*` que le backend NVIDIA — consommation comprise —
pour qu'une meme ligne de configuration fonctionne quelle que soit la carte.
Les sondes thermiques et la puissance viennent du sous-repertoire hwmon de la
carte ; `detect_backends` demande alors au backend hwmon generique d'ignorer ce
peripherique, pour ne pas publier deux fois la meme sonde sous deux noms.
"""

from __future__ import annotations

from pathlib import Path

from overlay.models import Group, Kind, Reading
from overlay.sensors.base import SensorBackend

DRM_ROOT = Path("/sys/class/drm")
_MIB = 1024 * 1024

#: Intitule de la sonde thermique principale d'un GPU AMD (les autres sont
#: « junction » pour le point chaud du die et « mem » pour la VRAM).
_TEMP_PRINCIPALE = "edge"


def _read_number(path: Path) -> float | None:
    try:
        return float(path.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return None


def _read_text(path: Path) -> str | None:
    try:
        value = path.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return value or None


def _read_name(device: Path) -> str | None:
    for candidate in ("product_name", "device"):
        if value := _read_text(device / candidate):
            return value
    return None


def _slug(text: str) -> str:
    out = [c.lower() if c.isalnum() else "_" for c in text.strip()]
    return "".join(out).strip("_") or "unknown"


class AmdGpuBackend(SensorBackend):
    name = "amdgpu"
    description = "GPU AMD sous Linux (sysfs amdgpu) : charge, VRAM, temperature, consommation"

    def __init__(self, root: Path | str = DRM_ROOT, *, index_offset: int = 0) -> None:
        self.root = Path(root)
        # Decalage d'index quand des GPU NVIDIA occupent deja `gpu.0`, `gpu.1`…
        # sur une machine hybride. Sans lui, les deux cartes se masqueraient.
        self.index_offset = index_offset

    # --- Decouverte ----------------------------------------------------------

    def _cards(self) -> list[Path]:
        if not self.root.is_dir():
            return []
        try:
            entries = sorted(self.root.iterdir())
        except OSError:
            return []
        cards = []
        for entry in entries:
            # `card0` oui, `card0-DP-1` (connecteur d'affichage) non.
            if not entry.name.startswith("card") or "-" in entry.name:
                continue
            device = entry / "device"
            if (device / "gpu_busy_percent").exists():
                cards.append(device)
        return cards

    @staticmethod
    def _hwmon_dir(device: Path) -> Path | None:
        """Sous-repertoire hwmon de la carte, ou vivent temperature et puissance."""
        root = device / "hwmon"
        if not root.is_dir():
            return None
        try:
            for entry in sorted(root.iterdir()):
                if entry.name.startswith("hwmon"):
                    return entry
        except OSError:
            return None
        return None

    def hwmon_devices(self) -> set[Path]:
        """Peripheriques hwmon couverts ici, que le backend generique doit ignorer."""
        found = set()
        for device in self._cards():
            if (hwmon := self._hwmon_dir(device)) is not None:
                found.add(hwmon.resolve())
        return found

    def available(self) -> bool:
        return bool(self._cards())

    # --- Lecture -------------------------------------------------------------

    def read(self) -> list[Reading]:
        readings: list[Reading] = []
        for position, device in enumerate(self._cards()):
            index = position + self.index_offset
            name = _read_name(device) or f"GPU AMD {index}"
            readings.extend(self._read_card(device, index, {"device": name}))
        return readings

    def _read_card(self, device: Path, index: int, extra: dict[str, str]) -> list[Reading]:
        readings: list[Reading] = []
        prefix = f"gpu.{index}"

        busy = _read_number(device / "gpu_busy_percent")
        if busy is not None:
            readings.append(
                self._make(f"{prefix}.load", f"GPU {index} charge", busy, "%",
                           Kind.LOAD, 0.0, 100.0, extra)
            )
        mem_busy = _read_number(device / "mem_busy_percent")
        if mem_busy is not None:
            readings.append(
                self._make(f"{prefix}.vram.load", f"GPU {index} bus memoire", mem_busy, "%",
                           Kind.LOAD, 0.0, 100.0, extra)
            )
        used = _read_number(device / "mem_info_vram_used")
        total = _read_number(device / "mem_info_vram_total")
        if used is not None:
            readings.append(
                self._make(f"{prefix}.vram.used", f"GPU {index} VRAM", used / _MIB, "MiB",
                           Kind.MEMORY, 0.0, total / _MIB if total else None, extra)
            )

        hwmon = self._hwmon_dir(device)
        if hwmon is not None:
            readings.extend(self._read_hwmon(hwmon, prefix, index, extra))
        return readings

    def _read_hwmon(
        self, hwmon: Path, prefix: str, index: int, extra: dict[str, str]
    ) -> list[Reading]:
        readings: list[Reading] = []

        # Consommation instantanee. Les cartes recentes exposent `power1_input`,
        # les plus anciennes une moyenne glissante dans `power1_average`.
        for fichier in ("power1_input", "power1_average"):
            micro_watts = _read_number(hwmon / fichier)
            if micro_watts is None:
                continue
            cap = _read_number(hwmon / "power1_cap")
            readings.append(
                self._make(
                    f"{prefix}.power",
                    f"GPU {index} consommation",
                    micro_watts / 1_000_000.0,
                    "W",
                    Kind.POWER,
                    0.0,
                    cap / 1_000_000.0 if cap else None,
                    extra,
                )
            )
            break

        for numero in range(1, 6):
            milli_degres = _read_number(hwmon / f"temp{numero}_input")
            if milli_degres is None:
                continue
            label = _read_text(hwmon / f"temp{numero}_label") or _TEMP_PRINCIPALE
            critique = _read_number(hwmon / f"temp{numero}_crit")
            # La sonde « edge » est la temperature GPU au sens courant : elle prend
            # la cle courte, les autres sont suffixees par leur intitule.
            cle = f"{prefix}.temp"
            libelle = f"GPU {index} temperature"
            if label.lower() != _TEMP_PRINCIPALE:
                cle = f"{prefix}.temp.{_slug(label)}"
                libelle = f"GPU {index} temperature {label}"
            readings.append(
                self._make(cle, libelle, milli_degres / 1000.0, "°C", Kind.TEMPERATURE,
                           None, critique / 1000.0 if critique else 95.0, extra)
            )

        # `gpu.N.fan` est un pourcentage chez NVIDIA : on aligne AMD sur le PWM et
        # on publie la vitesse reelle a part.
        pwm = _read_number(hwmon / "pwm1")
        if pwm is not None:
            readings.append(
                self._make(f"{prefix}.fan", f"GPU {index} ventilateur",
                           pwm / 255.0 * 100.0, "%", Kind.LOAD, 0.0, 100.0, extra)
            )
        rpm = _read_number(hwmon / "fan1_input")
        if rpm is not None:
            maximum = _read_number(hwmon / "fan1_max")
            readings.append(
                self._make(f"{prefix}.fan.rpm", f"GPU {index} ventilateur", rpm, "RPM",
                           Kind.FAN, 0.0, maximum or 3000.0, extra)
            )
        return readings

    def _make(
        self,
        key: str,
        label: str,
        value: float,
        unit: str,
        kind: Kind,
        minimum: float | None,
        maximum: float | None,
        extra: dict[str, str],
    ) -> Reading:
        return Reading(
            key=key,
            label=label,
            value=round(value, 1),
            unit=unit,
            group=Group.GPU,
            kind=kind,
            minimum=minimum,
            maximum=round(maximum, 1) if maximum is not None else None,
            source=self.name,
            extra=extra,
        )
