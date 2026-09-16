"""Consommation du processeur via powercap/RAPL.

Les domaines sysfs s'appellent `intel-rapl:0` : le deux-points est interdit dans
un nom de fichier sous Windows, et ces tests reproduisent l'arborescence reelle.
Comme le backend n'est de toute facon instancie que sous Linux, ils y sont limites.
"""

import sys
import time

import pytest

from overlay.models import Group, Kind
from overlay.sensors.linux_rapl import LinuxRaplBackend

pytestmark = pytest.mark.skipif(
    not sys.platform.startswith("linux"),
    reason="arborescence sysfs Linux (noms de domaine contenant « : »)",
)

PLAGE = 262_143_328_850


def domaine(chemin, nom, energie_uj, *, plage=PLAGE, limite_uw=None):
    chemin.mkdir(parents=True, exist_ok=True)
    (chemin / "name").write_text(str(nom))
    (chemin / "energy_uj").write_text(str(energie_uj))
    (chemin / "max_energy_range_uj").write_text(str(plage))
    if limite_uw is not None:
        (chemin / "constraint_0_power_limit_uw").write_text(str(limite_uw))
    return chemin


@pytest.fixture
def boitier(tmp_path):
    """Un processeur mono-socket avec ses sous-domaines coeurs et memoire."""
    package = domaine(tmp_path / "intel-rapl:0", "package-0", 1_000_000_000,
                      limite_uw=125_000_000)
    domaine(package / "intel-rapl:0:0", "core", 700_000_000)
    domaine(package / "intel-rapl:0:1", "dram", 90_000_000)
    return tmp_path


def avancer(chemin, energie_supplementaire):
    """Simule la consommation ecoulee en incrementant le compteur d'energie."""
    actuel = float((chemin / "energy_uj").read_text())
    (chemin / "energy_uj").write_text(str(actuel + energie_supplementaire))


def test_absence_de_powercap(tmp_path):
    backend = LinuxRaplBackend(tmp_path / "nulle-part")
    assert not backend.available()
    assert backend.read() == []


def test_disponible_quand_un_boitier_est_lisible(boitier):
    assert LinuxRaplBackend(boitier).available()


def test_premiere_lecture_sans_mesure(boitier):
    """RAPL compte de l'energie : il faut deux lectures pour en tirer une puissance."""
    assert LinuxRaplBackend(boitier).read() == []


def test_consommation_deduite_de_deux_lectures(boitier):
    backend = LinuxRaplBackend(boitier)
    backend.read()
    debut = time.monotonic()
    time.sleep(0.2)
    avancer(boitier / "intel-rapl:0", 45_000_000)  # 45 J
    mesures = {r.key: r for r in backend.read()}
    ecoule = time.monotonic() - debut

    consommation = mesures["cpu.power"]
    assert consommation.value == pytest.approx(45.0 / ecoule, rel=0.15)
    assert consommation.unit == "W"
    assert consommation.group is Group.CPU
    assert consommation.kind is Kind.POWER
    assert consommation.to_dict()["gauge"] is True


def test_limite_du_processeur_en_pleine_echelle(boitier):
    backend = LinuxRaplBackend(boitier)
    backend.read()
    time.sleep(0.05)
    avancer(boitier / "intel-rapl:0", 5_000_000)
    assert {r.key: r for r in backend.read()}["cpu.power"].range == (0.0, 125.0)


def test_limite_par_defaut_sans_contrainte_annoncee(tmp_path):
    package = domaine(tmp_path / "intel-rapl:0", "package-0", 0)
    backend = LinuxRaplBackend(tmp_path)
    backend.read()
    time.sleep(0.05)
    avancer(package, 1_000_000)
    assert {r.key: r for r in backend.read()}["cpu.power"].range == (0.0, 250.0)


def test_sous_domaines_publies_des_le_second_cycle(boitier):
    """Les sous-compteurs doivent etre amorces des la premiere lecture."""
    backend = LinuxRaplBackend(boitier)
    backend.read()
    time.sleep(0.1)
    avancer(boitier / "intel-rapl:0", 30_000_000)
    avancer(boitier / "intel-rapl:0" / "intel-rapl:0:0", 20_000_000)
    avancer(boitier / "intel-rapl:0" / "intel-rapl:0:1", 4_000_000)
    mesures = {r.key: r for r in backend.read()}

    assert {"cpu.power", "cpu.power.core", "cpu.power.dram"} <= set(mesures)
    assert mesures["cpu.power.core"].label == "CPU consommation coeurs"
    assert mesures["cpu.power.dram"].label == "CPU consommation memoire"
    # Un sous-domaine est un sous-ensemble du boitier, jamais un supplement.
    assert mesures["cpu.power.core"].value < mesures["cpu.power"].value


def test_rebouclage_du_compteur(tmp_path):
    """Le compteur revient a zero en quelques dizaines de minutes : delta negatif."""
    package = domaine(tmp_path / "intel-rapl:0", "package-0", PLAGE - 328_850)
    backend = LinuxRaplBackend(tmp_path)
    backend.read()
    time.sleep(0.1)
    (package / "energy_uj").write_text(str(20_000_000))  # repasse par zero
    mesures = {r.key: r for r in backend.read()}
    # 328 850 µJ avant le rebouclage, puis 20 J : la valeur reste positive et finie.
    assert mesures["cpu.power"].value > 0


def test_rebouclage_sans_plage_connue(tmp_path):
    package = tmp_path / "intel-rapl:0"
    package.mkdir()
    (package / "name").write_text("package-0")
    (package / "energy_uj").write_text("1000000")
    backend = LinuxRaplBackend(tmp_path)
    backend.read()
    time.sleep(0.05)
    (package / "energy_uj").write_text("10")
    # Sans max_energy_range_uj, le delta est ininterpretable : rien plutot qu'un faux.
    assert backend.read() == []


def test_compteur_mmio_ignore(tmp_path):
    """Sur certains Intel, le meme boitier est expose deux fois : MSR et MMIO."""
    domaine(tmp_path / "intel-rapl:0", "package-0", 1_000_000)
    domaine(tmp_path / "intel-rapl-mmio:0", "package-0", 1_000_000)
    backend = LinuxRaplBackend(tmp_path)
    backend.read()
    time.sleep(0.05)
    avancer(tmp_path / "intel-rapl:0", 2_000_000)
    avancer(tmp_path / "intel-rapl-mmio:0", 2_000_000)
    mesures = backend.read()
    assert [r.key for r in mesures] == ["cpu.power"]


def test_multi_socket(tmp_path):
    for index in (0, 1):
        domaine(tmp_path / f"intel-rapl:{index}", f"package-{index}", 1_000_000_000,
                limite_uw=90_000_000)
    backend = LinuxRaplBackend(tmp_path)
    backend.read()
    time.sleep(0.1)
    avancer(tmp_path / "intel-rapl:0", 30_000_000)
    avancer(tmp_path / "intel-rapl:1", 20_000_000)
    mesures = {r.key: r for r in backend.read()}

    assert {"cpu.power", "cpu.package.0.power", "cpu.package.1.power"} <= set(mesures)
    # Le total est la somme des boitiers, et la pleine echelle celle des deux limites.
    assert mesures["cpu.power"].value == pytest.approx(
        mesures["cpu.package.0.power"].value + mesures["cpu.package.1.power"].value,
        abs=0.2,
    )
    assert mesures["cpu.power"].range == (0.0, 180.0)


def test_pas_de_detail_par_boitier_en_mono_socket(boitier):
    backend = LinuxRaplBackend(boitier)
    backend.read()
    time.sleep(0.05)
    avancer(boitier / "intel-rapl:0", 5_000_000)
    assert not any(r.key.startswith("cpu.package.") for r in backend.read())


def test_domaine_sans_prefixe_package_ignore(tmp_path):
    """Seuls les domaines « package-N » sont des boitiers processeur."""
    domaine(tmp_path / "intel-rapl:0", "psys", 1_000_000)
    assert not LinuxRaplBackend(tmp_path).available()


def test_compteur_illisible_signale_une_seule_fois(tmp_path, caplog, monkeypatch):
    """Depuis la CVE-2020-8694, ces compteurs sont souvent reserves a root."""
    domaine(tmp_path / "intel-rapl:0", "package-0", 1_000_000)
    backend = LinuxRaplBackend(tmp_path)

    original = type(tmp_path).read_text

    def refuser(self, *args, **kwargs):
        if self.name == "energy_uj":
            raise PermissionError(13, "Permission denied")
        return original(self, *args, **kwargs)

    monkeypatch.setattr(type(tmp_path), "read_text", refuser)
    with caplog.at_level("WARNING"):
        assert not backend.available()
        assert backend.read() == []
        backend.read()

    avertissements = [r for r in caplog.records if "energy_uj" in r.getMessage()]
    assert len(avertissements) == 1
    assert "chmod" in avertissements[0].getMessage()
