"""Capteurs Windows via LibreHardwareMonitor.

Windows n'expose aucune API publique pour les temperatures et les ventilateurs :
il faut un pilote en mode noyau. LibreHardwareMonitor (open source, successeur
d'OpenHardwareMonitor) fournit ce pilote et un petit serveur web interne.

Prerequis cote utilisateur : lancer LibreHardwareMonitor en administrateur puis
activer `Options > Remote Web Server > Run`. Le backend interroge ensuite
`http://127.0.0.1:8085/data.json`, ce qui evite tout interop .NET cote Python.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any

from overlay.models import Group, Kind, Reading
from overlay.sensors.base import SensorBackend, to_float

DEFAULT_URL = "http://127.0.0.1:8085/data.json"

#: Correspondance entre le champ `Type` de LibreHardwareMonitor et notre modele.
_TYPE_MAP: dict[str, tuple[Kind, str]] = {
    "Temperature": (Kind.TEMPERATURE, "°C"),
    "Load": (Kind.LOAD, "%"),
    "Fan": (Kind.FAN, "RPM"),
    "Control": (Kind.LOAD, "%"),
    "Clock": (Kind.FREQUENCY, "MHz"),
    "Power": (Kind.POWER, "W"),
    "Voltage": (Kind.VOLTAGE, "V"),
    "Data": (Kind.MEMORY, "GiB"),
    "SmallData": (Kind.MEMORY, "MiB"),
    "Throughput": (Kind.RATE, "MiB/s"),
}

_GPU_TOKENS = ("gpu", "nvidia", "radeon", "geforce", "intel arc")
_CPU_TOKENS = ("cpu", "ryzen", "core i", "intel core", "amd fx", "threadripper")
_STORAGE_TOKENS = ("ssd", "nvme", "hdd", "samsung", "wd ", "crucial", "disk")

# Intitules exacts des sondes "processeur entier" (par opposition aux sondes par
# coeur ou par CCD), verifies dans le code source de LibreHardwareMonitor : AmdCpu.cs
# (Amd0FCpu/Amd10Cpu/Amd17Cpu) et IntelCpu.cs. Par ordre de preference : la sonde la
# plus proche de ce que les autres outils (Ryzen Master, HWiNFO, Intel XTU) appellent
# "la" temperature ou "la" puissance du processeur passe en premier.
_CPU_TEMP_GLOBALE = (
    "CPU Package",  # Intel : temperature du boitier entier.
    "Core (Tctl/Tdie)",  # AMD Ryzen (Zen 2+) : capteur combine, le plus courant.
    "Core (Tctl)",  # AMD Ryzen (Zen/Zen+) : cible de regulation des ventilateurs.
    "Core (Tdie)",  # AMD Ryzen : die reel, legerement sous Tctl.
    "Core Max",  # Intel, repli : maximum instantane entre coeurs.
    "Core Average",  # Intel, repli : moyenne entre coeurs.
)
_CPU_POWER_GLOBALE = (
    "CPU Package",  # Intel : somme coeurs + cache + controleur memoire integre.
    "Package",  # AMD Ryzen : consommation du boitier entier (capteur SMU).
)


def _slug(text: str) -> str:
    out = [c.lower() if c.isalnum() else "_" for c in text.strip()]
    return "".join(out).strip("_") or "unknown"


def _classify(hardware: str, sensor_type: str, label: str) -> Group:
    haystack = f"{hardware} {label}".lower()
    if sensor_type in ("Fan", "Control") and not any(t in haystack for t in _GPU_TOKENS):
        return Group.FAN
    if any(token in haystack for token in _GPU_TOKENS):
        return Group.GPU
    if any(token in haystack for token in _CPU_TOKENS):
        return Group.CPU
    if "memory" in haystack or "ram" in haystack:
        return Group.MEMORY
    if any(token in haystack for token in _STORAGE_TOKENS):
        return Group.STORAGE
    if "network" in haystack or "ethernet" in haystack or "wi-fi" in haystack:
        return Group.NETWORK
    return Group.SYSTEM


def parse_tree(node: dict[str, Any], source: str = "lhm") -> list[Reading]:
    """Aplati l'arbre `data.json` de LibreHardwareMonitor en une liste de mesures.

    L'arbre a la forme Racine > Machine > Materiel > Categorie > Capteur ; seuls les
    noeuds portant un `SensorId` sont des mesures.
    """
    readings: list[Reading] = []
    seen: set[str] = set()

    def walk(current: dict[str, Any], hardware: str) -> None:
        text = str(current.get("Text", "")).strip()
        sensor_type = str(current.get("Type", "")).strip()
        children = current.get("Children") or []

        if current.get("SensorId") and sensor_type in _TYPE_MAP:
            kind, unit = _TYPE_MAP[sensor_type]
            value = to_float(current.get("Value"))
            maximum = to_float(current.get("Max"))
            key = f"lhm.{_slug(hardware)}.{_slug(sensor_type)}.{_slug(text)}"
            # Un meme intitule peut apparaitre deux fois sur un materiel : on suffixe.
            if key in seen:
                suffix = 2
                while f"{key}.{suffix}" in seen:
                    suffix += 1
                key = f"{key}.{suffix}"
            seen.add(key)
            readings.append(
                Reading(
                    key=key,
                    label=f"{hardware} {text}".strip(),
                    value=value,
                    unit=unit,
                    group=_classify(hardware, sensor_type, text),
                    kind=kind,
                    maximum=maximum if kind is Kind.TEMPERATURE else None,
                    source=source,
                    extra={"hardware": hardware, "sensorId": current["SensorId"], "text": text},
                )
            )
            return

        # Les deux premiers niveaux sont la racine et la machine : le materiel est
        # le premier noeud qui possede lui-meme des categories de capteurs.
        next_hardware = hardware
        if children and not hardware and sensor_type == "":
            next_hardware = text if text and text.lower() != "sensor" else hardware
        for child in children:
            if isinstance(child, dict):
                walk(child, next_hardware)

    for child in node.get("Children") or []:
        if not isinstance(child, dict):
            continue
        # `child` est la machine ; ses enfants directs sont les composants.
        for hardware_node in child.get("Children") or []:
            if isinstance(hardware_node, dict):
                walk(hardware_node, str(hardware_node.get("Text", "")).strip())
    return readings


def _sonde_globale(
    readings: list[Reading], *, kind: Kind, priorite: tuple[str, ...]
) -> Reading | None:
    """Repere la sonde "processeur entier" parmi les mesures deja classees CPU.

    Cherche une correspondance exacte sur l'intitule brut de la sonde (le champ
    `Text` de LibreHardwareMonitor, conserve dans `extra["text"]"), dans l'ordre de
    `priorite`. Sans correspondance, ne devine rien plutot que de risquer d'aliaser
    une sonde par coeur ou par CCD, ce qui serait plus trompeur que son absence.
    """
    candidats = {
        r.extra.get("text"): r
        for r in readings
        if r.group is Group.CPU and r.kind is kind and r.extra.get("text")
    }
    for texte in priorite:
        if trouve := candidats.get(texte):
            return trouve
    return None


def _aliaser_cpu(readings: list[Reading], *, source: str) -> list[Reading]:
    """Ajoute `cpu.temp` et `cpu.power` a cote des cles `lhm.*` d'origine.

    Sans cela, aucune mesure LibreHardwareMonitor ne repond jamais a ces cles :
    leur intitule depend du modele de processeur (« Core (Tctl/Tdie) », « CPU
    Package »...), donc aucune configuration par defaut ne peut les cibler
    directement. `cpu.temp`/`cpu.power` sont les cles attendues par l'overlay et
    l'application mobile sur toutes les plateformes ; la sonde d'origine reste
    presente sous sa cle `lhm.*`, pour qui veut le detail complet.
    """
    alias: list[Reading] = []
    plans = (
        ("cpu.temp", "CPU temperature", "°C", Kind.TEMPERATURE, _CPU_TEMP_GLOBALE),
        ("cpu.power", "CPU consommation", "W", Kind.POWER, _CPU_POWER_GLOBALE),
    )
    for key, label, unit, kind, priorite in plans:
        origine = _sonde_globale(readings, kind=kind, priorite=priorite)
        if origine is None or origine.value is None:
            continue
        alias.append(
            Reading(
                key=key,
                label=label,
                value=origine.value,
                unit=unit,
                group=Group.CPU,
                kind=kind,
                minimum=origine.minimum,
                maximum=origine.maximum,
                source=source,
                extra={"alias_de": origine.key},
            )
        )
    return alias


class LibreHardwareMonitorBackend(SensorBackend):
    name = "lhm"
    description = "LibreHardwareMonitor (Windows) : temperatures, ventilateurs, puissances"

    def __init__(self, url: str = DEFAULT_URL, *, timeout: float = 2.0) -> None:
        self.url = url
        self.timeout = timeout

    def _fetch(self) -> dict[str, Any] | None:
        try:
            with urllib.request.urlopen(self.url, timeout=self.timeout) as response:  # noqa: S310
                payload = response.read()
        except (urllib.error.URLError, OSError, TimeoutError):
            return None
        try:
            data = json.loads(payload)
        except (json.JSONDecodeError, UnicodeDecodeError):
            return None
        return data if isinstance(data, dict) else None

    def available(self) -> bool:
        return self._fetch() is not None

    def read(self) -> list[Reading]:
        data = self._fetch()
        if data is None:
            return []
        readings = parse_tree(data, source=self.name)
        readings.extend(_aliaser_cpu(readings, source=self.name))
        return readings
