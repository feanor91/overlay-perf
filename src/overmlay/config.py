"""Chargement et validation de la configuration (fichier TOML + valeurs par defaut)."""

from __future__ import annotations

import contextlib
import secrets
import sys
from dataclasses import asdict, dataclass, field, fields, is_dataclass
from pathlib import Path
from typing import Any

from platformdirs import user_config_path, user_data_path

if sys.version_info >= (3, 11):
    import tomllib
else:  # pragma: no cover - chemin Python 3.10
    import tomli as tomllib

APP_NAME = "overmlay"

#: Mesures affichees par defaut dans l'overlay, dans l'ordre.
DEFAULT_OVERLAY_METRICS: tuple[str, ...] = (
    "fps.current",
    "fps.low1",
    "fps.frametime",
    "cpu.load",
    "cpu.temp",
    "temp.coretemp.*",
    "temp.k10temp.*",
    "gpu.0.load",
    "gpu.0.temp",
    "gpu.0.vram.used",
    "memory.load",
    "fan.*",
)

VALID_POSITIONS = ("top-left", "top-right", "bottom-left", "bottom-right")
VALID_FPS_MODES = ("auto", "presentmon", "mangohud", "push", "off")


def config_path() -> Path:
    return user_config_path(APP_NAME, appauthor=False) / "config.toml"


def data_path() -> Path:
    return user_data_path(APP_NAME, appauthor=False)


@dataclass(slots=True)
class GeneralConfig:
    #: Periode d'echantillonnage des capteurs, en secondes.
    poll_interval: float = 1.0
    #: Force le backend de demonstration (aucun materiel requis).
    mock: bool = False
    #: Nombre de snapshots conserves pour les graphiques d'historique.
    history_size: int = 300


@dataclass(slots=True)
class SensorsConfig:
    per_core: bool = False
    include_io: bool = True
    #: Noms de backends a ne pas charger : psutil, hwmon, amdgpu, nvidia, lhm.
    disabled: list[str] = field(default_factory=list)
    lhm_url: str = "http://127.0.0.1:8085/data.json"


@dataclass(slots=True)
class FpsConfig:
    mode: str = "auto"
    presentmon_path: str = ""
    mangohud_log_dir: str = ""
    window_seconds: float = 1.0


@dataclass(slots=True)
class ServerConfig:
    enabled: bool = True
    #: 0.0.0.0 rend le serveur joignable depuis le telephone sur le reseau local.
    host: str = "0.0.0.0"
    port: int = 8777
    #: Jeton partage exige par l'API ; genere et persiste automatiquement si vide.
    token: str = ""
    #: Intervalle de diffusion WebSocket ; 0 suit la cadence des capteurs.
    push_interval: float = 0.0
    metrics: list[str] = field(default_factory=list)


@dataclass(slots=True)
class OverlayConfig:
    enabled: bool = True
    position: str = "top-left"
    margin: int = 24
    opacity: float = 0.85
    font_size: int = 14
    columns: int = 1
    #: Laisse passer clics et mouvements de souris vers la fenetre situee dessous.
    click_through: bool = True
    hotkey: str = "<ctrl>+<alt>+o"
    visible_at_start: bool = True
    metrics: list[str] = field(default_factory=lambda: list(DEFAULT_OVERLAY_METRICS))


@dataclass(slots=True)
class Config:
    general: GeneralConfig = field(default_factory=GeneralConfig)
    sensors: SensorsConfig = field(default_factory=SensorsConfig)
    fps: FpsConfig = field(default_factory=FpsConfig)
    server: ServerConfig = field(default_factory=ServerConfig)
    overlay: OverlayConfig = field(default_factory=OverlayConfig)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


class ConfigError(ValueError):
    """Configuration syntaxiquement valide mais incoherente."""


def _apply_section(section: Any, values: dict[str, Any], name: str) -> None:
    """Recopie `values` dans la dataclass `section` en controlant les types."""
    known = {f.name: f for f in fields(section)}
    for key, value in values.items():
        if key not in known:
            raise ConfigError(f"[{name}] option inconnue : {key!r}")
        current = getattr(section, key)
        if isinstance(current, bool) and not isinstance(value, bool):
            raise ConfigError(f"[{name}] {key} attend un booleen")
        if isinstance(current, (int, float)) and not isinstance(current, bool):
            if not isinstance(value, (int, float)) or isinstance(value, bool):
                raise ConfigError(f"[{name}] {key} attend un nombre")
            # TOML distingue 1 de 1.0 : on aligne sur le type de la valeur par defaut.
            value = type(current)(value)
        if isinstance(current, str) and not isinstance(value, str):
            raise ConfigError(f"[{name}] {key} attend une chaine")
        if isinstance(current, list) and (
            not isinstance(value, list) or not all(isinstance(v, str) for v in value)
        ):
            raise ConfigError(f"[{name}] {key} attend une liste de chaines")
        setattr(section, key, value)


def _validate(config: Config) -> None:
    if config.general.poll_interval <= 0:
        raise ConfigError("[general] poll_interval doit etre strictement positif")
    if config.general.history_size < 1:
        raise ConfigError("[general] history_size doit valoir au moins 1")
    if config.fps.mode not in VALID_FPS_MODES:
        raise ConfigError(f"[fps] mode doit etre parmi {', '.join(VALID_FPS_MODES)}")
    if not 1 <= config.server.port <= 65535:
        raise ConfigError("[server] port doit etre compris entre 1 et 65535")
    if config.overlay.position not in VALID_POSITIONS:
        raise ConfigError(f"[overlay] position doit etre parmi {', '.join(VALID_POSITIONS)}")
    if not 0.05 <= config.overlay.opacity <= 1.0:
        raise ConfigError("[overlay] opacity doit etre compris entre 0.05 et 1.0")
    if config.overlay.columns < 1:
        raise ConfigError("[overlay] columns doit valoir au moins 1")


def load_config(path: Path | str | None = None) -> Config:
    """Charge la configuration ; un fichier absent donne les valeurs par defaut."""
    config = Config()
    target = Path(path) if path else config_path()
    if not target.is_file():
        return config

    with target.open("rb") as handle:
        raw = tomllib.load(handle)

    for name in (f.name for f in fields(Config)):
        section = raw.pop(name, None)
        if section is None:
            continue
        if not isinstance(section, dict):
            raise ConfigError(f"[{name}] doit etre une table TOML")
        target_section = getattr(config, name)
        assert is_dataclass(target_section)
        _apply_section(target_section, section, name)
    if raw:
        raise ConfigError(f"Sections inconnues : {', '.join(sorted(raw))}")

    _validate(config)
    return config


def resolve_token(config: Config, *, create: bool = True) -> str:
    """Retourne le jeton d'acces a l'API.

    Priorite au fichier de configuration ; sinon un jeton aleatoire est genere une
    fois puis conserve dans le repertoire de donnees, en lecture seule proprietaire.
    """
    if config.server.token:
        return config.server.token

    token_file = data_path() / "token"
    if token_file.is_file():
        existing = token_file.read_text(encoding="utf-8").strip()
        if existing:
            return existing
    if not create:
        return ""

    token = secrets.token_urlsafe(24)
    token_file.parent.mkdir(parents=True, exist_ok=True)
    token_file.write_text(token, encoding="utf-8")
    # Systemes de fichiers sans permissions POSIX (FAT, certains montages Windows).
    with contextlib.suppress(OSError):
        token_file.chmod(0o600)
    return token
