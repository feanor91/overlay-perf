"""Backends de capteurs et detection automatique du materiel disponible."""

from __future__ import annotations

import logging
import sys

from overlay.sensors.amdgpu import AmdGpuBackend
from overlay.sensors.base import SensorBackend
from overlay.sensors.linux_hwmon import LinuxHwmonBackend
from overlay.sensors.linux_rapl import LinuxRaplBackend
from overlay.sensors.mock import MockBackend
from overlay.sensors.nvidia import NvidiaBackend
from overlay.sensors.psutil_backend import PsutilBackend
from overlay.sensors.windows_lhm import LibreHardwareMonitorBackend

log = logging.getLogger(__name__)

__all__ = [
    "AmdGpuBackend",
    "LibreHardwareMonitorBackend",
    "LinuxHwmonBackend",
    "LinuxRaplBackend",
    "MockBackend",
    "NvidiaBackend",
    "PsutilBackend",
    "SensorBackend",
    "detect_backends",
]

#: Sous Windows, aucune API publique n'expose temperatures et ventilateurs : sans
#: LibreHardwareMonitor joignable, ces mesures manquent en silence si personne ne le
#: signale. Le message rappelle la marche a suivre verifiee dans son propre code
#: source (case a cocher persistante, demarrage minimise avec Windows).
_LHM_INDISPONIBLE = (
    "LibreHardwareMonitor injoignable sur {url} : les temperatures, ventilateurs et "
    "consommations cote carte mere resteront absents.\n"
    "  1. Lancez LibreHardwareMonitor en administrateur.\n"
    "  2. Menu Options > Remote Web Server > Run.\n"
    "  3. Pour ne plus y penser : Options > Start Minimized, puis Run on Windows Startup."
)

_AUCUN_CAPTEUR = "Aucun capteur detecte : bascule sur le backend de demonstration."


def detect_backends(
    *,
    per_core: bool = False,
    include_io: bool = True,
    disabled: frozenset[str] | set[str] | tuple[str, ...] = (),
    lhm_url: str | None = None,
    force_mock: bool = False,
    warnings: list[str] | None = None,
) -> list[SensorBackend]:
    """Construit la liste des backends exploitables sur la machine courante.

    Le backend psutil sert de socle portable. Quand une source plus precise couvre
    deja le thermique (hwmon sous Linux, LibreHardwareMonitor sous Windows), on
    desactive les temperatures de psutil pour ne pas publier deux fois la meme sonde.

    `warnings`, si fourni, recoit un message actionnable pour chaque source attendue
    mais absente (LibreHardwareMonitor injoignable, par exemple). Sans lui, le meme
    message part dans les journaux au niveau INFO : la CLI le passe pour l'afficher
    clairement au demarrage plutot que de laisser les mesures manquer sans explication.
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
        rapl = LinuxRaplBackend()
        if "rapl" not in disabled and rapl.available():
            candidates.append(rapl)
    elif sys.platform == "win32" and "lhm" not in disabled:
        # Construit uniquement si la source n'est pas explicitement desactivee : la
        # desactiver ne doit declencher ni requete HTTP ni avertissement.
        lhm = LibreHardwareMonitorBackend(lhm_url) if lhm_url else LibreHardwareMonitorBackend()
        if lhm.available():
            candidates.append(lhm)
            thermal_source = True
        else:
            _signaler(warnings, _LHM_INDISPONIBLE.format(url=lhm.url))

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
        _signaler(warnings, _AUCUN_CAPTEUR, niveau=logging.WARNING)
        candidates.append(MockBackend())
    return candidates


def _signaler(warnings: list[str] | None, message: str, *, niveau: int = logging.INFO) -> None:
    """Route un message vers la liste structuree si fournie, sinon vers les journaux.

    Eviter le doublon est volontaire : un appelant qui recueille `warnings` (la CLI)
    l'affichera lui-meme clairement, un appelant qui ne le fait pas garde au moins la
    trace dans les journaux.
    """
    if warnings is not None:
        warnings.append(message)
    else:
        log.log(niveau, message)
