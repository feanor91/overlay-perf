import io

import pytest

from overmlay.fps.sources import (
    MangoHudSource,
    PresentMonSource,
    build_frame_source,
    consume_presentmon_csv,
    parse_mangohud_line,
)
from overmlay.fps.tracker import FpsBackend, FrameTimeTracker, percentile
from overmlay.models import Group, Kind

CSV_V1 = """Application,ProcessID,SwapChainAddress,Runtime,Dropped,TimeInSeconds,msBetweenPresents
jeu.exe,1234,0x1,DXGI,0,1.000,16.68
jeu.exe,1234,0x1,DXGI,0,1.016,16.71
jeu.exe,1234,0x1,DXGI,0,1.033,33.20
"""

CSV_V2 = """Application,ProcessID,SwapChainAddress,PresentRuntime,FrameType,CPUStartTime,FrameTime
jeu.exe,99,0x2,DXGI,Application,1.000,8.33
jeu.exe,99,0x2,DXGI,Application,1.008,8.31
jeu.exe,99,0x2,DXGI,Application,1.016,N/A
"""


# --- Centiles --------------------------------------------------------------


def test_centile_par_interpolation():
    valeurs = [float(v) for v in range(1, 101)]
    assert percentile(valeurs, 0.0) == 1.0
    assert percentile(valeurs, 1.0) == 100.0
    assert percentile(valeurs, 0.99) == pytest.approx(99.01)


def test_centile_sur_une_seule_valeur():
    assert percentile([12.0], 0.99) == 12.0


def test_centile_sur_liste_vide():
    with pytest.raises(ValueError):
        percentile([], 0.5)


# --- Suivi des trames ------------------------------------------------------


def test_sans_trame_les_mesures_sont_absentes():
    stats = FrameTimeTracker().stats()
    assert stats.fps is None and stats.stale is True


def test_moyenne_et_temps_de_trame(tracker):
    for _ in range(120):
        tracker.add_frame_time(8.0)
    stats = tracker.stats()
    assert stats.fps == pytest.approx(125.0, abs=0.5)
    assert stats.frame_time_ms == pytest.approx(8.0, abs=0.01)
    assert stats.frame_count == 120


def test_les_centiles_bas_decrivent_les_trames_lentes(tracker):
    for _ in range(980):
        tracker.add_frame_time(10.0)   # 100 FPS
    for _ in range(20):
        tracker.add_frame_time(100.0)  # 10 FPS : 2 % des trames
    stats = tracker.stats()
    assert stats.low_1_percent == pytest.approx(10.0, abs=1.0)
    assert stats.fps > stats.low_1_percent  # la moyenne masque les saccades


def test_les_centiles_exigent_assez_d_echantillons(tracker):
    for _ in range(10):
        tracker.add_frame_time(16.0)
    stats = tracker.stats()
    assert stats.fps is not None
    assert stats.low_1_percent is None


def test_horodatages_successifs(tracker):
    for index in range(11):
        tracker.add_frame(index * 0.02)  # 50 FPS
    assert tracker.stats().fps == pytest.approx(50.0, abs=0.5)


def test_horodatage_non_monotone_ignore(tracker):
    tracker.add_frame(10.0)
    tracker.add_frame(10.0)  # trame dupliquee : ecart nul
    tracker.add_frame(9.0)   # horloge qui recule
    assert tracker.stats().frame_count == 0
    # La trame fautive devient la nouvelle reference : la suivante compte de nouveau.
    tracker.add_frame(9.016)
    assert tracker.stats().frame_count == 1


def test_coupure_de_flux_ecartee_des_centiles(tracker):
    tracker.add_frame(10.0)
    tracker.add_frame(13.0)  # 3 s : alt-tab ou ecran de chargement, pas une trame
    assert tracker.stats().frame_count == 0
    tracker.add_frame_time(5000.0)
    assert tracker.stats().frame_count == 0


def test_duree_de_trame_nulle_ou_negative_ignoree(tracker):
    tracker.add_frame_time(0.0)
    tracker.add_frame_time(-5.0)
    assert tracker.stats().frame_count == 0


def test_flux_interrompu_devient_perime(monkeypatch):
    """Le jeu ferme : au-dela du delai, le compteur ne doit plus rien afficher.

    L'horloge est pilotee explicitement : sous Windows, avant Python 3.13,
    time.monotonic() avance par pas d'environ 15 ms et deux appels consecutifs
    peuvent renvoyer la meme valeur, ce qui rendrait le resultat aleatoire.
    """
    instant = [1000.0]
    monkeypatch.setattr("overmlay.fps.tracker.time.monotonic", lambda: instant[0])

    tracker = FrameTimeTracker(window_seconds=1.0, stale_after=2.0)
    tracker.add_frame_time(16.0)
    assert tracker.stats().stale is False

    instant[0] += 2.5  # plus aucune trame depuis plus de stale_after
    stats = tracker.stats()
    assert stats.stale is True
    assert stats.fps is None
    # L'historique demeure : il servira si le flux reprend.
    assert stats.frame_count == 1


def test_reinitialisation(tracker):
    tracker.add_frame_time(16.0, application="jeu.exe")
    tracker.reset()
    assert tracker.stats().frame_count == 0
    assert tracker.stats().application is None


def test_fenetre_glissante_ecarte_les_anciennes_trames():
    import time

    tracker = FrameTimeTracker(window_seconds=0.05, stale_after=60.0)
    tracker.add_frame_time(16.0)
    assert tracker.stats().fps is not None
    time.sleep(0.12)
    # Hors fenetre : plus rien a moyenner, mais l'historique reste pour les centiles.
    assert tracker.stats().fps is None
    assert tracker.stats().frame_count == 1


def test_horodatages_externes_alimentent_la_fenetre_courante():
    """Les dates fournies par PresentMon n'ont pas la meme base que l'horloge locale."""
    tracker = FrameTimeTracker(window_seconds=0.5, stale_after=60.0)
    for index in range(11):
        tracker.add_frame(1_000_000.0 + index * 0.02)
    assert tracker.stats().fps == pytest.approx(50.0, abs=0.5)


def test_capacite_bornee():
    tracker = FrameTimeTracker(capacity=50, stale_after=60.0)
    for _ in range(200):
        tracker.add_frame_time(10.0)
    assert tracker.stats().frame_count == 50


def test_fenetre_invalide_refusee():
    with pytest.raises(ValueError):
        FrameTimeTracker(window_seconds=0)


# --- Exposition en mesures -------------------------------------------------


def test_backend_fps_publie_quatre_mesures(tracker):
    for _ in range(300):
        tracker.add_frame_time(8.0, application="jeu.exe")
    mesures = {r.key: r for r in FpsBackend(tracker).read()}
    assert set(mesures) == {"fps.current", "fps.frametime", "fps.low1", "fps.low01"}
    assert mesures["fps.current"].group is Group.FPS
    assert mesures["fps.frametime"].kind is Kind.DURATION
    assert mesures["fps.current"].extra["application"] == "jeu.exe"


def test_backend_fps_sans_source_renvoie_des_valeurs_vides():
    mesures = FpsBackend(FrameTimeTracker()).read()
    assert all(r.value is None for r in mesures)


# --- PresentMon ------------------------------------------------------------


@pytest.mark.parametrize(
    ("csv", "trames", "duree"),
    [(CSV_V1, 3, 22.2), (CSV_V2, 2, 8.32)],
    ids=["presentmon-v1", "presentmon-v2"],
)
def test_lecture_des_deux_formats_presentmon(csv, trames, duree, tracker):
    assert consume_presentmon_csv(io.StringIO(csv), tracker) == trames
    stats = tracker.stats()
    assert stats.frame_count == trames
    assert stats.frame_time_ms == pytest.approx(duree, abs=0.05)
    assert stats.application == "jeu.exe"


def test_csv_sans_colonne_de_duree_ne_produit_rien(tracker):
    csv = "Application,ProcessID\njeu.exe,1\n"
    assert consume_presentmon_csv(io.StringIO(csv), tracker) == 0


def test_entete_rejoue_en_cours_de_flux(tracker):
    # PresentMon reemet son en-tete quand une nouvelle session demarre.
    flux = CSV_V2 + CSV_V1
    consume_presentmon_csv(io.StringIO(flux), tracker)
    assert tracker.stats().frame_count == 5


def test_arret_demande_interrompt_la_lecture(tracker):
    consume_presentmon_csv(io.StringIO(CSV_V1), tracker, should_stop=lambda: True)
    assert tracker.stats().frame_count == 0


# --- MangoHud --------------------------------------------------------------


COLONNES = ["fps", "frametime", "cpu_load", "gpu_load", "cpu_temp", "gpu_temp", "elapsed"]


def test_ligne_mangohud_en_microsecondes():
    assert parse_mangohud_line("59.88,16700,23,71,58,64,1200", COLONNES) == (59.88, 16.7)


def test_ligne_mangohud_sans_duree_deduit_depuis_le_fps():
    fps, duree = parse_mangohud_line("120.0,,23,71,58,64,1200", COLONNES)
    assert fps == 120.0
    assert duree == pytest.approx(8.333, abs=0.01)


def test_ligne_mangohud_tronquee():
    assert parse_mangohud_line("59.88", COLONNES) == (None, None)


def test_source_mangohud_indisponible_sans_dossier(tmp_path, tracker):
    source = MangoHudSource(tracker, tmp_path / "absent")
    assert not source.available()
    assert source.start() is False


def test_source_mangohud_choisit_le_journal_le_plus_recent(tmp_path, tracker):
    (tmp_path / "ancien.csv").write_text("fps,frametime\n")
    recent = tmp_path / "recent.csv"
    recent.write_text("fps,frametime\n")
    import os
    import time

    os.utime(tmp_path / "ancien.csv", (time.time() - 60, time.time() - 60))
    source = MangoHudSource(tracker, tmp_path)
    assert source.available()
    assert source._newest_log() == recent


def test_choix_de_la_source_selon_le_mode(tracker, tmp_path):
    assert build_frame_source(tracker, mode="push") is None
    assert build_frame_source(tracker, mode="mangohud", mangohud_log_dir=tmp_path / "x") is None
    source = build_frame_source(tracker, mode="mangohud", mangohud_log_dir=tmp_path)
    assert isinstance(source, MangoHudSource)


def test_presentmon_absent_du_path(tracker, monkeypatch):
    monkeypatch.setattr("shutil.which", lambda _: None)
    assert not PresentMonSource(tracker).available()
