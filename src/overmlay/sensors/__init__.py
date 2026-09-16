"""Backends de capteurs et detection automatique du materiel disponible."""

from __future__ import annotations

import logging
import sys

from overmlay.sensors.amdgpu import AmdGpuBackend
from overmlay.sensors.base import SensorBackend
from overmlay.sensors.linux_hwmon import LinuxHwmonBackend
from overmlay.sensors.mock import MockBackend
from overmlay.sensors.nvidia import NvidiaBackend
from overmlay.sensors.psutil_backend import PsutilBackend
from overmlay.sensors.windows_lhm import LibreHardwareMonitorBackend

log = logging.getLogger(__name__)

__all__ = [
    "AmdGpuBackend",
    "LibreHardwareMonitorBackend",
    "LinuxHwmonBackend",
    "MockBackend",
    "NvidiaBackend",
    "PsutilBackend",
    "SensorBackend",
    "detect_backends",
]


def detect_backends(
    *,
    per_core: bool = False,
    include_io: bool = True,
    disabled: frozenset[str] | set[str] | tuple[str, ...] = (),
    lhm_url: str | None = None,
    force_mock: bool = False,
) -> list[SensorBackend]:
    """Construit la liste des backends exploitables sur la machine courante.

    Le backend psutil sert de socle portable. Quand une source plus precise couvre
    deja le thermique (hwmon sous Linux, LibreHardwareMonitor sous Windows), on
    desactive les temperatures de psutil pour ne pas publier deux fois la meme sonde.
    """
    disabled = set(disabled)
    if force_mock:
        return [MockBackend()]

    candidates: list[SensorBackend] = []
    thermal_source = False

    nvidia = NvidiaBackend()
    nvidia_actif = "nvidia" not in disabled and nvidia.available()

    if sys.platform.startswith("linux"):
        # Les cartes AMD sont numerotees a la suite des cartes NVIDIA : sur une
        # machine hybride, les deux revendiqueraient sinon la cle `gpu.0`.
        amd = AmdGpuBackend(index_offset=nvidia.device_count() if nvidia_actif else 0)
        exclusions: set = set()
        if "amdgpu" not in disabled and amd.available():
            candidates.append(amd)
            # Temperature, consommation et ventilateur du GPU AMD sont deja publies
            # ci-dessus sous `gpu.N.*` : hwmon ne doit pas les republier.
            exclusions = amd.hwmon_devices()
        hwmon = LinuxHwmonBackend(exclude=exclusions)
        if "hwmon" not in disabled and hwmon.available():
            candidates.append(hwmon)
            thermal_source = True
    elif sys.platform == "win32":
        lhm = LibreHardwareMonitorBackend(lhm_url) if lhm_url else LibreHardwareMonitorBackend()
        if "lhm" not in disabled and lhm.available():
            candidates.append(lhm)
            thermal_source = True
        else:
            log.info(
                "LibreHardwareMonitor injoignable sur %s : pas de temperature ni de "
                "vitesse de ventilateur cote carte mere. Lancez-le en administrateur "
                "avec 'Remote Web Server' active.",
                lhm.url,
            )

    if nvidia_actif:
        candidates.append(nvidia)

    if "psutil" not in disabled:
        candidates.append(
            PsutilBackend(
                per_core=per_core,
                include_io=include_io,
                include_thermals=not thermal_source,
            )
        )

    if not candidates:
        log.warning("Aucun capteur detecte : bascule sur le backend de demonstration.")
        candidates.append(MockBackend())
    return candidates
