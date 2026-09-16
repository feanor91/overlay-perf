import time

import pytest

from overmlay.hub import merge_readings
from overmlay.models import Group, Kind
from overmlay.sensors import detect_backends
from overmlay.sensors.amdgpu import AmdGpuBackend
from overmlay.sensors.base import SensorBackend, to_float
from overmlay.sensors.linux_hwmon import LinuxHwmonBackend
from overmlay.sensors.mock import MockBackend
from overmlay.sensors.nvidia import NvidiaBackend, parse_smi_csv
from overmlay.sensors.psutil_backend import PsutilBackend
from overmlay.sensors.windows_lhm import parse_tree


@pytest.mark.parametrize(
    ("brute", "attendu"),
    [
        ("53.0 C", 53.0),
        ("1200RPM", 1200.0),
        ("  +61.2 degC  ", 61.2),
        ("62,3 °C", 62.3),          # separateur decimal francais de Windows
        ("1,234.5 MHz", 1234.5),    # separateur de milliers anglais
        ("N/A", None),
        ("[Not Supported]", None),
        ("", None),
        (None, None),
        (42, 42.0),
        (True, 1.0),
    ],
)
def test_conversion_des_valeurs_brutes(brute, attendu):
    assert to_float(brute) == attendu


def test_une_panne_de_capteur_ne_remonte_pas(caplog):
    class Casse(SensorBackend):
        name = "casse"

        def read(self):
            raise OSError("sonde retiree")

    backend = Casse()
    assert backend.safe_read() == []
    assert backend._failures == 1


def test_les_echecs_repetes_ne_saturent_pas_les_journaux(caplog):
    class Casse(SensorBackend):
        name = "casse"
        _LOG_EVERY = 5

        def read(self):
            raise OSError("sonde retiree")

    backend = Casse()
    with caplog.at_level("WARNING"):
        for _ in range(12):
            backend.safe_read()
    # 1 trace complete + un rappel a la 5e et a la 10e occurrence.
    assert len(caplog.records) == 3
    assert backend._failures == 12


# --- hwmon -----------------------------------------------------------------


@pytest.fixture
def faux_hwmon(tmp_path):
    cpu = tmp_path / "hwmon0"
    cpu.mkdir()
    (cpu / "name").write_text("coretemp")
    (cpu / "temp1_input").write_text("61000")
    (cpu / "temp1_label").write_text("Package id 0")
    (cpu / "temp1_crit").write_text("100000")

    carte = tmp_path / "hwmon1"
    carte.mkdir()
    (carte / "name").write_text("nct6798")
    (carte / "fan1_input").write_text("1240")
    (carte / "fan1_label").write_text("CPU Fan")
    (carte / "pwm1").write_text("128")

    gpu = tmp_path / "hwmon2"
    gpu.mkdir()
    (gpu / "name").write_text("amdgpu")
    (gpu / "temp1_input").write_text("54000")
    (gpu / "power1_average").write_text("145000000")
    return tmp_path


def test_lecture_hwmon(faux_hwmon):
    backend = LinuxHwmonBackend(faux_hwmon)
    assert backend.available()
    mesures = {r.key: r for r in backend.read()}

    package = mesures["temp.coretemp.package_id_0"]
    assert package.value == 61.0
    assert package.unit == "°C"
    assert package.group is Group.CPU
    assert package.range[1] == 100.0  # temp1_crit, converti des millidegres

    ventilateur = mesures["fan.nct6798.1.rpm"]
    assert ventilateur.value == 1240.0 and ventilateur.group is Group.FAN
    assert mesures["fan.nct6798.1.pwm"].value == pytest.approx(50.2, abs=0.1)

    # Une sonde amdgpu est classee en GPU, pas en carte mere.
    assert mesures["temp.amdgpu.amdgpu_temp1"].group is Group.GPU
    assert mesures["power.amdgpu.1"].value == 145.0


def test_hwmon_ignore_les_fichiers_illisibles(faux_hwmon):
    (faux_hwmon / "hwmon0" / "temp2_input").write_text("pas un nombre")
    mesures = {r.key for r in LinuxHwmonBackend(faux_hwmon).read()}
    assert "temp.coretemp.package_id_0" in mesures
    assert not any(cle.endswith("temp2") for cle in mesures)


def test_hwmon_absent(tmp_path):
    assert not LinuxHwmonBackend(tmp_path / "nulle-part").available()


# --- NVIDIA ----------------------------------------------------------------


def test_analyse_csv_nvidia_smi():
    sortie = (
        "0, NVIDIA GeForce RTX 4070, 54, 37, 12, 2048, 12282, 41, 78.55, 200.00, 1920, 10501\n"
        "1, NVIDIA T400, 45, [N/A], 0, 100, 2048, [Not Supported], [N/A], [N/A], 300, 400\n"
    )
    lignes = parse_smi_csv(sortie)
    assert len(lignes) == 2
    assert lignes[0]["name"] == "NVIDIA GeForce RTX 4070"
    assert lignes[0]["power.draw"] == 78.55
    assert lignes[1]["utilization.gpu"] is None


def test_csv_nvidia_tronque_ignore():
    assert parse_smi_csv("0, GPU, 54\n\n") == []


def test_mesures_nvidia_omettent_les_champs_non_supportes():
    ligne = parse_smi_csv(
        "1, NVIDIA T400, 45, [N/A], 0, 100, 2048, [Not Supported], [N/A], [N/A], 300, 400"
    )[0]
    backend = NvidiaBackend.__new__(NvidiaBackend)
    cles = {r.key for r in backend._to_readings(ligne)}
    assert "gpu.1.temp" in cles
    assert "gpu.1.load" not in cles      # utilization.gpu vaut [N/A]
    assert "gpu.1.fan" not in cles       # ventilateur absent sur cette carte


def test_consommation_nvidia():
    ligne = parse_smi_csv(
        "0, RTX 4070, 54, 37, 12, 2048, 12282, 41, 78.55, 200.00, 1920, 10501"
    )[0]
    backend = NvidiaBackend.__new__(NvidiaBackend)
    consommation = next(r for r in backend._to_readings(ligne) if r.key == "gpu.0.power")
    assert consommation.value == 78.5
    assert consommation.unit == "W"
    assert consommation.kind is Kind.POWER
    # La limite de la carte donne la pleine echelle de la jauge.
    assert consommation.range == (0.0, 200.0)


def test_vram_bornee_par_la_capacite_de_la_carte():
    ligne = parse_smi_csv(
        "0, RTX 4070, 54, 37, 12, 2048, 12282, 41, 78.55, 200.00, 1920, 10501"
    )[0]
    backend = NvidiaBackend.__new__(NvidiaBackend)
    vram = next(r for r in backend._to_readings(ligne) if r.key == "gpu.0.vram.used")
    assert vram.range == (0.0, 12282.0)


# --- LibreHardwareMonitor --------------------------------------------------


def arbre_lhm():
    return {
        "Text": "Sensor",
        "Children": [
            {
                "Text": "DESKTOP",
                "Children": [
                    {
                        "Text": "AMD Ryzen 7 5800X",
                        "Children": [
                            {
                                "Text": "Temperatures",
                                "Children": [
                                    {
                                        "Text": "Core (Tctl/Tdie)",
                                        "Value": "62,3 °C",
                                        "Max": "89,1 °C",
                                        "Type": "Temperature",
                                        "SensorId": "/amdcpu/0/temperature/2",
                                    }
                                ],
                            }
                        ],
                    },
                    {
                        "Text": "Nuvoton NCT6798D",
                        "Children": [
                            {
                                "Text": "Fans",
                                "Children": [
                                    {
                                        "Text": "Fan #1",
                                        "Value": "820 RPM",
                                        "Type": "Fan",
                                        "SensorId": "/lpc/nct6798d/fan/0",
                                    },
                                    {
                                        "Text": "Fan #1",
                                        "Value": "910 RPM",
                                        "Type": "Fan",
                                        "SensorId": "/lpc/nct6798d/fan/1",
                                    },
                                ],
                            }
                        ],
                    },
                    {
                        "Text": "NVIDIA GeForce RTX 4070",
                        "Children": [
                            {
                                "Text": "Fans",
                                "Children": [
                                    {
                                        "Text": "GPU Fan",
                                        "Value": "1450 RPM",
                                        "Type": "Fan",
                                        "SensorId": "/gpu-nvidia/0/fan/0",
                                    }
                                ],
                            }
                        ],
                    },
                ],
            }
        ],
    }


def test_aplatissement_de_l_arbre_lhm():
    mesures = {r.key: r for r in parse_tree(arbre_lhm())}
    coeur = mesures["lhm.amd_ryzen_7_5800x.temperature.core__tctl_tdie"]
    assert coeur.value == 62.3
    assert coeur.group is Group.CPU
    assert coeur.kind is Kind.TEMPERATURE
    assert coeur.range[1] == 89.1


def test_lhm_deduplique_les_intitules_identiques():
    cles = [r.key for r in parse_tree(arbre_lhm()) if r.key.startswith("lhm.nuvoton")]
    assert len(cles) == len(set(cles)) == 2


def test_lhm_classe_le_ventilateur_gpu_avec_le_gpu():
    mesures = {r.key: r for r in parse_tree(arbre_lhm())}
    assert mesures["lhm.nvidia_geforce_rtx_4070.fan.gpu_fan"].group is Group.GPU
    assert mesures["lhm.nuvoton_nct6798d.fan.fan__1"].group is Group.FAN


def test_arbre_lhm_vide():
    assert parse_tree({}) == []
    assert parse_tree({"Children": []}) == []


# --- amdgpu ----------------------------------------------------------------


@pytest.fixture
def faux_amdgpu(tmp_path):
    device = tmp_path / "card0" / "device"
    device.mkdir(parents=True)
    (device / "gpu_busy_percent").write_text("73")
    (device / "mem_busy_percent").write_text("41")
    (device / "mem_info_vram_used").write_text(str(6 * 1024 * 1024 * 1024))
    (device / "mem_info_vram_total").write_text(str(16 * 1024 * 1024 * 1024))
    (device / "product_name").write_text("Radeon RX 7800 XT")

    hwmon = device / "hwmon" / "hwmon4"
    hwmon.mkdir(parents=True)
    (hwmon / "name").write_text("amdgpu")
    (hwmon / "power1_average").write_text("187000000")  # 187 W
    (hwmon / "power1_cap").write_text("263000000")
    (hwmon / "temp1_input").write_text("64000")
    (hwmon / "temp1_label").write_text("edge")
    (hwmon / "temp2_input").write_text("78000")
    (hwmon / "temp2_label").write_text("junction")
    (hwmon / "pwm1").write_text("140")
    (hwmon / "fan1_input").write_text("1620")

    # Un connecteur d'affichage ne doit pas etre pris pour une carte.
    (tmp_path / "card0-DP-1").mkdir()
    return tmp_path


def test_lecture_amdgpu(faux_amdgpu):
    backend = AmdGpuBackend(faux_amdgpu)
    assert backend.available()
    mesures = {r.key: r for r in backend.read()}
    assert mesures["gpu.0.load"].value == 73.0
    assert mesures["gpu.0.vram.load"].value == 41.0
    assert mesures["gpu.0.vram.used"].value == 6144.0
    assert mesures["gpu.0.vram.used"].range == (0.0, 16384.0)


def test_consommation_amdgpu(faux_amdgpu):
    """La cle est la meme que chez NVIDIA : une seule ligne de configuration suffit."""
    consommation = {r.key: r for r in AmdGpuBackend(faux_amdgpu).read()}["gpu.0.power"]
    assert consommation.value == 187.0
    assert consommation.unit == "W"
    assert consommation.kind is Kind.POWER
    assert consommation.group is Group.GPU
    assert consommation.range == (0.0, 263.0)  # power1_cap sert de pleine echelle
    assert consommation.to_dict()["gauge"] is True


def test_consommation_amdgpu_depuis_power1_input(tmp_path):
    """Les cartes recentes exposent `power1_input` la ou les anciennes moyennaient."""
    hwmon = tmp_path / "card0" / "device" / "hwmon" / "hwmon0"
    hwmon.mkdir(parents=True)
    (tmp_path / "card0" / "device" / "gpu_busy_percent").write_text("10")
    (hwmon / "power1_input").write_text("95500000")
    mesures = {r.key: r for r in AmdGpuBackend(tmp_path).read()}
    assert mesures["gpu.0.power"].value == 95.5


def test_amdgpu_sans_capteur_de_consommation(tmp_path):
    device = tmp_path / "card0" / "device"
    device.mkdir(parents=True)
    (device / "gpu_busy_percent").write_text("10")
    mesures = {r.key for r in AmdGpuBackend(tmp_path).read()}
    assert mesures == {"gpu.0.load"}  # aucune cle inventee faute de sonde


def test_sondes_thermiques_amdgpu(faux_amdgpu):
    mesures = {r.key: r for r in AmdGpuBackend(faux_amdgpu).read()}
    # « edge » est la temperature GPU au sens courant : elle prend la cle courte.
    assert mesures["gpu.0.temp"].value == 64.0
    assert mesures["gpu.0.temp.junction"].value == 78.0
    # `gpu.N.fan` est un pourcentage chez NVIDIA : AMD s'aligne sur le PWM.
    assert mesures["gpu.0.fan"].unit == "%"
    assert mesures["gpu.0.fan"].value == pytest.approx(54.9, abs=0.1)
    assert mesures["gpu.0.fan.rpm"].value == 1620.0


def test_decalage_d_index_sur_machine_hybride(faux_amdgpu):
    """Avec un GPU NVIDIA en `gpu.0`, la carte AMD prend `gpu.1`."""
    mesures = {r.key for r in AmdGpuBackend(faux_amdgpu, index_offset=1).read()}
    assert "gpu.1.power" in mesures
    assert not any(cle.startswith("gpu.0.") for cle in mesures)


def test_hwmon_du_gpu_signale_pour_exclusion(faux_amdgpu):
    devices = AmdGpuBackend(faux_amdgpu).hwmon_devices()
    assert {p.name for p in devices} == {"hwmon4"}


def test_hwmon_ignore_les_peripheriques_exclus(faux_hwmon):
    """Sans exclusion, la sonde du GPU serait publiee deux fois sous deux noms."""
    complet = {r.key for r in LinuxHwmonBackend(faux_hwmon).read()}
    assert any(cle.startswith("temp.amdgpu") for cle in complet)

    exclu = {
        r.key
        for r in LinuxHwmonBackend(
            faux_hwmon, exclude={(faux_hwmon / "hwmon2").resolve()}
        ).read()
    }
    assert not any(cle.startswith("temp.amdgpu") for cle in exclu)
    assert "temp.coretemp.package_id_0" in exclu  # les autres puces restent lues


def test_pas_de_doublon_entre_amdgpu_et_hwmon(tmp_path):
    """Reproduit le sysfs reel : /sys/class/hwmon pointe vers le hwmon de la carte.

    Sans exclusion, la consommation du GPU remonterait deux fois — une fois en
    `gpu.0.power`, une fois en `power.amdgpu.1` — sous deux intitules differents.
    """
    drm = tmp_path / "drm"
    device = drm / "card0" / "device"
    hwmon_carte = device / "hwmon" / "hwmon4"
    hwmon_carte.mkdir(parents=True)
    (device / "gpu_busy_percent").write_text("73")
    (hwmon_carte / "name").write_text("amdgpu")
    (hwmon_carte / "power1_average").write_text("187000000")
    (hwmon_carte / "temp1_input").write_text("64000")
    (hwmon_carte / "temp1_label").write_text("edge")

    # Le repertoire global n'expose que des liens vers les peripheriques reels.
    classe = tmp_path / "hwmon"
    classe.mkdir()
    try:
        (classe / "hwmon2").symlink_to(hwmon_carte, target_is_directory=True)
    except OSError:  # Windows sans mode developpeur ni droits administrateur
        pytest.skip("creation de lien symbolique non autorisee sur cette plateforme")
    carte_mere = classe / "hwmon0"
    carte_mere.mkdir()
    (carte_mere / "name").write_text("coretemp")
    (carte_mere / "temp1_input").write_text("45000")

    amd = AmdGpuBackend(drm)
    hwmon = LinuxHwmonBackend(classe, exclude=amd.hwmon_devices())
    mesures = merge_readings([amd.safe_read(), hwmon.safe_read()])
    cles = [r.key for r in mesures]

    assert len(cles) == len(set(cles))
    assert "gpu.0.power" in cles
    assert not any(cle.startswith("power.amdgpu") for cle in cles)
    assert not any(cle.startswith("temp.amdgpu") for cle in cles)
    # La carte mere, elle, est toujours lue.
    assert "temp.coretemp.coretemp_temp1" in cles


def test_amdgpu_absent(tmp_path):
    assert not AmdGpuBackend(tmp_path).available()
    assert AmdGpuBackend(tmp_path).read() == []
    assert AmdGpuBackend(tmp_path).hwmon_devices() == set()


# --- psutil et detection ---------------------------------------------------


def test_backend_psutil_produit_les_mesures_de_base():
    backend = PsutilBackend(per_core=True, include_io=False)
    cles = {r.key for r in backend.read()}
    assert {"cpu.load", "memory.load", "memory.used", "memory.swap"} <= cles
    assert any(cle.startswith("cpu.core.") for cle in cles)


def test_les_debits_exigent_deux_lectures():
    backend = PsutilBackend(include_io=True, include_thermals=False)
    assert not any(r.key.startswith("network.") for r in backend.read())
    # L'horloge doit avoir avance : sous Windows, avant Python 3.13, elle progresse
    # par pas d'environ 15 ms et deux appels consecutifs tombent sur le meme tic.
    time.sleep(0.05)
    assert any(r.key.startswith("network.") for r in backend.read())


def test_une_horloge_grossiere_ne_fait_pas_disparaitre_les_debits(monkeypatch):
    """La reference ne doit pas etre avancee tant qu'aucun temps ne s'est ecoule.

    Sinon, sur une horloge a faible resolution, chaque relevé repousserait la
    reference et les debits ne seraient jamais calcules.
    """
    instant = [1000.0]
    monkeypatch.setattr(
        "overmlay.sensors.psutil_backend.time.monotonic", lambda: instant[0]
    )
    backend = PsutilBackend(include_io=True, include_thermals=False)
    backend.read()  # pose la reference

    for _ in range(5):
        assert not any(r.key.startswith("network.") for r in backend.read())

    instant[0] += 0.0156  # un tic d'horloge Windows
    assert any(r.key.startswith("network.") for r in backend.read())


def test_backend_simule_toujours_disponible():
    mesures = MockBackend(seed=7).read()
    assert len(mesures) > 5
    assert all(r.value is not None for r in mesures)
    assert {r.group for r in mesures} >= {Group.CPU, Group.GPU, Group.FAN}


def test_detection_force_le_mode_simule():
    backends = detect_backends(force_mock=True)
    assert [b.name for b in backends] == ["mock"]


def test_detection_respecte_les_backends_desactives():
    tous = {"psutil", "hwmon", "nvidia", "amdgpu", "lhm"}
    noms = {b.name for b in detect_backends(disabled=tous)}
    assert noms == {"mock"}  # repli automatique quand plus rien n'est disponible
