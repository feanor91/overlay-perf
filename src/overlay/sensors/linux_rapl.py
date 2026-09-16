"""Consommation du processeur sous Linux, via l'interface powercap (RAPL).

Contrairement aux autres sondes, RAPL n'expose pas une puissance mais un compteur
d'energie cumule en microjoules : la puissance se deduit de la variation entre deux
lectures. Une premiere lecture ne produit donc rien, et le compteur reboucle
regulierement — `max_energy_range_uj` sert a rattraper ce retour a zero.

Depuis la CVE-2020-8694, la plupart des distributions reservent la lecture de ces
compteurs a root, une mesure fine de la consommation permettant des attaques par
canal auxiliaire. Le backend se desactive proprement dans ce cas, en expliquant une
seule fois comment y remedier.
"""

from __future__ import annotations

import logging
import re
import time
from pathlib import Path

from overlay.models import Group, Kind, Reading
from overlay.sensors.base import SensorBackend

log = logging.getLogger(__name__)

POWERCAP_ROOT = Path("/sys/class/powercap")

#: Un domaine de premier niveau ressemble a `intel-rapl:0` ; ses sous-domaines
#: (coeurs, memoire…) ajoutent un second indice, `intel-rapl:0:1`.
_PACKAGE_RE = re.compile(r"^[a-z-]+:\d+$")

#: Les compteurs `-mmio` doublonnent les compteurs MSR du meme boitier.
_CONTROL_EXCLU = "mmio"

#: Puissance maximale affichee quand le noyau n'annonce aucune limite.
_LIMITE_PAR_DEFAUT = 250.0

_LIBELLES_DOMAINES = {
    "core": "coeurs",
    "uncore": "uncore",
    "dram": "memoire",
    "psys": "plateforme",
}


def _slug(text: str) -> str:
    out = [c.lower() if c.isalnum() else "_" for c in text.strip()]
    return "".join(out).strip("_") or "unknown"


class LinuxRaplBackend(SensorBackend):
    name = "rapl"
    description = "Consommation du processeur sous Linux (powercap/RAPL)"

    def __init__(self, root: Path | str = POWERCAP_ROOT) -> None:
        self.root = Path(root)
        # Cle de domaine -> (instant de lecture, energie cumulee en microjoules).
        self._previous: dict[str, tuple[float, float]] = {}
        self._permission_refusee = False

    # --- Decouverte ----------------------------------------------------------

    def _packages(self) -> list[Path]:
        """Domaines de premier niveau, un par boitier processeur."""
        if not self.root.is_dir():
            return []
        try:
            entries = sorted(self.root.iterdir())
        except OSError:
            return []
        packages = []
        for entry in entries:
            if not _PACKAGE_RE.match(entry.name) or _CONTROL_EXCLU in entry.name:
                continue
            if not (entry / "energy_uj").exists():
                continue
            if (self._read_text(entry / "name") or "").startswith("package"):
                packages.append(entry)
        return packages

    @staticmethod
    def _subdomains(package: Path) -> list[Path]:
        try:
            entries = sorted(package.iterdir())
        except OSError:
            return []
        return [
            entry
            for entry in entries
            if entry.is_dir()
            and entry.name.startswith(f"{package.name}:")
            and (entry / "energy_uj").exists()
        ]

    @staticmethod
    def _read_text(path: Path) -> str | None:
        try:
            return path.read_text(encoding="utf-8").strip()
        except OSError:
            return None

    def _read_number(self, path: Path) -> float | None:
        try:
            return float(path.read_text(encoding="utf-8").strip())
        except PermissionError:
            if not self._permission_refusee:
                self._permission_refusee = True
                log.warning(
                    "Lecture de %s refusee : la consommation du processeur restera "
                    "indisponible. Ces compteurs sont reserves a root sur la plupart "
                    "des distributions ; pour les ouvrir en lecture : "
                    "sudo chmod a+r /sys/class/powercap/*/energy_uj (a refaire au "
                    "redemarrage, ou a fixer par une regle udev).",
                    path,
                )
            return None
        except (OSError, ValueError):
            return None

    def available(self) -> bool:
        """Un domaine doit exister *et* etre lisible par l'utilisateur courant."""
        packages = self._packages()
        if not packages:
            return False
        return any(self._read_number(p / "energy_uj") is not None for p in packages)

    # --- Lecture -------------------------------------------------------------

    def _power_watts(self, domain: Path) -> float | None:
        """Puissance moyenne depuis la lecture precedente, ou `None` a la premiere."""
        energie = self._read_number(domain / "energy_uj")
        if energie is None:
            return None
        maintenant = time.monotonic()
        cle = str(domain)
        precedent = self._previous.get(cle)
        self._previous[cle] = (maintenant, energie)
        if precedent is None:
            return None

        ecoule = maintenant - precedent[0]
        if ecoule <= 0:
            return None
        delta = energie - precedent[1]
        if delta < 0:
            # Le compteur a reboucle : on ajoute une periode complete.
            plage = self._read_number(domain / "max_energy_range_uj")
            if not plage:
                return None
            delta += plage
            if delta < 0:  # pragma: no cover - plage incoherente
                return None
        return (delta / 1_000_000.0) / ecoule

    def _limite_watts(self, domain: Path) -> float | None:
        micro_watts = self._read_number(domain / "constraint_0_power_limit_uw")
        return micro_watts / 1_000_000.0 if micro_watts else None

    def read(self) -> list[Reading]:
        packages = self._packages()
        if not packages:
            return []

        total = 0.0
        total_limite = 0.0
        mesure_valide = False
        par_domaine: dict[str, float] = {}
        par_package: list[tuple[int, float, float | None]] = []

        for index, package in enumerate(packages):
            puissance = self._power_watts(package)
            limite = self._limite_watts(package)
            if limite:
                total_limite += limite

            # Les sous-domaines sont interroges a chaque cycle, y compris celui ou
            # le boitier n'a pas encore de mesure : sans cela leur propre compteur
            # ne serait jamais amorce et ils resteraient une lecture en retard.
            for sous_domaine in self._subdomains(package):
                nom = self._read_text(sous_domaine / "name") or sous_domaine.name
                valeur = self._power_watts(sous_domaine)
                if valeur is not None:
                    # Un sous-domaine est un sous-ensemble du boitier : on le cumule
                    # entre boitiers, jamais avec le total.
                    par_domaine[nom] = par_domaine.get(nom, 0.0) + valeur

            if puissance is None:
                continue
            mesure_valide = True
            total += puissance
            par_package.append((index, puissance, limite))

        if not mesure_valide:
            return []

        readings = [
            self._make(
                "cpu.power",
                "CPU consommation",
                total,
                total_limite or _LIMITE_PAR_DEFAUT,
            )
        ]
        for nom, valeur in par_domaine.items():
            readings.append(
                self._make(
                    f"cpu.power.{_slug(nom)}",
                    f"CPU consommation {_LIBELLES_DOMAINES.get(nom, nom)}",
                    valeur,
                    total_limite or _LIMITE_PAR_DEFAUT,
                )
            )
        if len(par_package) > 1:
            # Machine multi-socket : le detail par boitier a un sens.
            for index, valeur, limite in par_package:
                readings.append(
                    self._make(
                        f"cpu.package.{index}.power",
                        f"CPU {index} consommation",
                        valeur,
                        limite or _LIMITE_PAR_DEFAUT,
                    )
                )
        return readings

    def _make(self, key: str, label: str, value: float, maximum: float) -> Reading:
        return Reading(
            key=key,
            label=label,
            value=round(value, 1),
            unit="W",
            group=Group.CPU,
            kind=Kind.POWER,
            minimum=0.0,
            maximum=round(maximum, 1),
            source=self.name,
        )
