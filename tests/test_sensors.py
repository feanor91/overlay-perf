import pytest

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


def test_lecture_amdgpu(tmp_path):
    device = tmp_path / "card0" / "device"
    device.mkdir(parents=True)
    (device / "gpu_busy_percent").write_text("73")
    (device / "mem_info_vram_used").write_text(str(6 * 1024 * 1024 * 1024))
    (device / "mem_info_vram_total").write_text(str(16 * 1024 * 1024 * 1024))
    # Un connecteur d'affichage ne doit pas etre pris pour une carte.
    (tmp_path / "card0-DP-1").mkdir()

    backend = AmdGpuBackend(tmp_path)
    assert backend.available()
    mesures = {r.key: r for r in backend.read()}
    assert mesures["gpu.0.load"].value == 73.0
    assert mesures["gpu.0.vram.used"].value == 6144.0
    assert mesures["gpu.0.vram.used"].range == (0.0, 16384.0)


def test_amdgpu_absent(tmp_path):
    assert not AmdGpuBackend(tmp_path).available()
    assert AmdGpuBackend(tmp_path).read() == []


# --- psutil et detection ---------------------------------------------------


def test_backend_psutil_produit_les_mesures_de_base():
    backend = PsutilBackend(per_core=True, include_io=False)
    cles = {r.key for r in backend.read()}
    assert {"cpu.load", "memory.load", "memory.used", "memory.swap"} <= cles
    assert any(cle.startswith("cpu.core.") for cle in cles)


def test_les_debits_exigent_deux_lectures():
    backend = PsutilBackend(include_io=True, include_thermals=False)
    assert not any(r.key.startswith("network.") for r in backend.read())
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
