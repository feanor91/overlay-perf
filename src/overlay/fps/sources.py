"""Sources de trames : PresentMon (Windows), MangoHud (Linux), poussee applicative.

Aucune de ces sources n'injecte de code dans les jeux : PresentMon s'appuie sur les
traces ETW de Windows, MangoHud est une couche Vulkan/OpenGL deja installee par
l'utilisateur, et la troisieme voie est l'API HTTP du serveur.
"""

from __future__ import annotations

import csv
import io
import logging
import os
import shutil
import subprocess
import threading
from pathlib import Path

from overlay.fps.tracker import FrameTimeTracker
from overlay.sensors.base import to_float

log = logging.getLogger(__name__)

#: Colonnes de duree de trame, par ordre de preference (PresentMon v2 puis v1).
_FRAME_TIME_COLUMNS = ("FrameTime", "msBetweenPresents", "msBetweenDisplayChange")
_APP_COLUMNS = ("Application", "ProcessName")


class FrameSource:
    """Producteur de trames tournant dans un thread demon."""

    name = "source"

    def __init__(self, tracker: FrameTimeTracker) -> None:
        self.tracker = tracker
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()

    def available(self) -> bool:
        return False

    def start(self) -> bool:
        if not self.available():
            return False
        self._stop.clear()
        self._thread = threading.Thread(target=self._guarded_run, name=f"fps-{self.name}",
                                        daemon=True)
        self._thread.start()
        return True

    def stop(self) -> None:
        self._stop.set()
        thread = self._thread
        if thread is not None and thread.is_alive():
            thread.join(timeout=2.0)
        self._thread = None

    def _guarded_run(self) -> None:
        try:
            self._run()
        except Exception:
            log.warning("Source FPS %s interrompue", self.name, exc_info=True)

    def _run(self) -> None:  # pragma: no cover - surcharge par les sous-classes
        raise NotImplementedError


def consume_presentmon_csv(stream: io.TextIOBase, tracker: FrameTimeTracker,
                           should_stop=lambda: False) -> int:
    """Lit un flux CSV PresentMon et alimente le tracker. Retourne le nombre de trames.

    Les versions 1.x et 2.x de PresentMon n'ont ni les memes colonnes ni le meme
    ordre : on se repere sur l'en-tete plutot que sur des indices fixes.
    """
    reader = csv.reader(stream)
    header: list[str] | None = None
    frame_column = app_column = -1
    count = 0

    for row in reader:
        if should_stop():
            break
        if not row:
            continue
        if header is None or row[0] in _APP_COLUMNS:
            header = [cell.strip() for cell in row]
            frame_column = next(
                (header.index(name) for name in _FRAME_TIME_COLUMNS if name in header), -1
            )
            app_column = next((header.index(name) for name in _APP_COLUMNS if name in header), -1)
            if frame_column < 0:
                log.warning("En-tete PresentMon sans colonne de temps de trame : %s", header)
            continue
        if frame_column < 0 or frame_column >= len(row):
            continue
        frame_time = to_float(row[frame_column])
        if frame_time is None or frame_time <= 0:
            continue
        application = row[app_column] if 0 <= app_column < len(row) else None
        tracker.add_frame_time(frame_time, application=application)
        count += 1
    return count


class PresentMonSource(FrameSource):
    """FPS sous Windows via PresentMon (Intel, open source).

    PresentMon ecoute les evenements ETW de presentation DXGI/D3D/Vulkan : il mesure
    donc tous les jeux, y compris en plein ecran exclusif, sans hook. Le processus
    doit tourner avec les droits administrateur.
    """

    name = "presentmon"

    def __init__(self, tracker: FrameTimeTracker, executable: str | None = None) -> None:
        super().__init__(tracker)
        self.executable = executable or self._find_executable()
        self._process: subprocess.Popen[str] | None = None

    @staticmethod
    def _find_executable() -> str | None:
        for candidate in ("PresentMon.exe", "presentmon.exe", "PresentMon", "presentmon"):
            found = shutil.which(candidate)
            if found:
                return found
        return None

    def available(self) -> bool:
        return self.executable is not None

    def stop(self) -> None:
        process = self._process
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:  # pragma: no cover - depend de l'OS
                process.kill()
        self._process = None
        super().stop()

    def _run(self) -> None:
        assert self.executable is not None
        command = [
            self.executable,
            "--output_stdout",
            "--stop_existing_session",
            "--no_top",
        ]
        self._process = subprocess.Popen(  # noqa: S603 - binaire resolu via shutil.which
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            bufsize=1,
        )
        if self._process.stdout is None:  # pragma: no cover - defensif
            return
        consume_presentmon_csv(self._process.stdout, self.tracker, self._stop.is_set)


def parse_mangohud_line(line: str, columns: list[str]) -> tuple[float | None, float | None]:
    """Extrait (fps, temps de trame en ms) d'une ligne de journal MangoHud.

    MangoHud journalise le temps de trame en microsecondes.
    """
    cells = [cell.strip() for cell in line.split(",")]
    if len(cells) < len(columns):
        return None, None
    values = dict(zip(columns, cells, strict=False))
    fps = to_float(values.get("fps"))
    frame_time_us = to_float(values.get("frametime"))
    frame_time_ms = frame_time_us / 1000.0 if frame_time_us else None
    if frame_time_ms is None and fps:
        frame_time_ms = 1000.0 / fps
    return fps, frame_time_ms


class MangoHudSource(FrameSource):
    """FPS sous Linux en suivant le journal CSV de MangoHud.

    Cote jeu il faut lancer avec la journalisation active, par exemple :
    `MANGOHUD_CONFIG=output_folder=~/.local/share/overlay,autostart_log=1 mangohud %command%`
    """

    name = "mangohud"

    def __init__(
        self,
        tracker: FrameTimeTracker,
        log_dir: Path | str,
        *,
        poll_interval: float = 0.25,
    ) -> None:
        super().__init__(tracker)
        self.log_dir = Path(log_dir).expanduser()
        self.poll_interval = poll_interval

    def available(self) -> bool:
        return self.log_dir.is_dir()

    def _newest_log(self) -> Path | None:
        try:
            candidates = [p for p in self.log_dir.glob("*.csv") if p.is_file()]
        except OSError:
            return None
        if not candidates:
            return None
        return max(candidates, key=lambda p: p.stat().st_mtime)

    def _run(self) -> None:
        current: Path | None = None
        handle: io.TextIOBase | None = None
        columns: list[str] = []
        application: str | None = None

        try:
            while not self._stop.is_set():
                newest = self._newest_log()
                if newest is not None and newest != current:
                    if handle is not None:
                        handle.close()
                    current = newest
                    application = newest.stem
                    handle = newest.open("r", encoding="utf-8", errors="replace")
                    handle.seek(0, os.SEEK_END)  # on ne rejoue pas l'historique
                    columns = []

                if handle is None:
                    self._stop.wait(self.poll_interval)
                    continue

                line = handle.readline()
                if not line:
                    self._stop.wait(self.poll_interval)
                    continue
                line = line.strip()
                if not line:
                    continue
                if line.startswith("fps,"):
                    columns = [cell.strip() for cell in line.split(",")]
                    continue
                if not columns:
                    continue  # en-tete pas encore rencontre (preambule materiel)
                _, frame_time_ms = parse_mangohud_line(line, columns)
                if frame_time_ms:
                    self.tracker.add_frame_time(frame_time_ms, application=application)
        finally:
            if handle is not None:
                handle.close()


def build_frame_source(
    tracker: FrameTimeTracker,
    *,
    mode: str = "auto",
    presentmon_path: str | None = None,
    mangohud_log_dir: Path | str | None = None,
) -> FrameSource | None:
    """Choisit la source de trames adaptee a la plateforme.

    `mode` vaut `auto`, `presentmon`, `mangohud` ou `push` (aucune source locale :
    seules les trames envoyees sur l'API HTTP sont prises en compte).
    """
    if mode == "push":
        return None
    if mode in ("auto", "presentmon"):
        source = PresentMonSource(tracker, presentmon_path)
        if source.available():
            return source
        if mode == "presentmon":
            log.warning("PresentMon introuvable : indiquez son chemin dans la configuration.")
            return None
    if mode in ("auto", "mangohud"):
        directory = mangohud_log_dir or Path.home() / ".local/share/overlay/mangohud"
        source = MangoHudSource(tracker, directory)
        if source.available():
            return source
        if mode == "mangohud":
            log.warning("Dossier de journaux MangoHud absent : %s", directory)
    return None
