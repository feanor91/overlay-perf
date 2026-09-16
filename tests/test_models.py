import math

import pytest

from overmlay.models import DEFAULT_RANGES, GAUGE_KINDS, Group, Kind, Reading, Snapshot
from tests.helpers import reading


def test_valeur_non_finie_devient_absente():
    for brute in (math.nan, math.inf, -math.inf):
        assert Reading(
            key="x", label="x", value=brute, unit="°C", group=Group.CPU, kind=Kind.TEMPERATURE
        ).value is None


def test_cle_vide_refusee():
    with pytest.raises(ValueError):
        Reading(key="", label="x", value=1.0, unit="%", group=Group.CPU, kind=Kind.LOAD)


def test_bornes_par_defaut_selon_le_type():
    temperature = reading("cpu.temp", 60.0, kind=Kind.TEMPERATURE, unit="°C")
    assert temperature.range == DEFAULT_RANGES[Kind.TEMPERATURE]
    explicite = reading("cpu.temp", 60.0, kind=Kind.TEMPERATURE, minimum=30.0, maximum=95.0)
    assert explicite.range == (30.0, 95.0)


def test_serialisation_expose_le_drapeau_de_jauge():
    charge = reading("cpu.load", 42.0).to_dict()
    assert charge["gauge"] is True
    assert charge["min"] == 0.0 and charge["max"] == 100.0

    horloge = reading("cpu.clock", 4200.0, kind=Kind.FREQUENCY, unit="MHz").to_dict()
    assert horloge["gauge"] is False
    assert Kind.FREQUENCY not in GAUGE_KINDS


def test_filtrage_par_cle_exacte_et_par_prefixe():
    snapshot = Snapshot.build(
        [
            reading("cpu.load"),
            reading("gpu.0.load", group=Group.GPU),
            reading("gpu.0.temp", group=Group.GPU, kind=Kind.TEMPERATURE),
            reading("memory.load", group=Group.MEMORY),
        ]
    )
    assert [r.key for r in snapshot.filter(["gpu.*"]).readings] == ["gpu.0.load", "gpu.0.temp"]
    assert [r.key for r in snapshot.filter(["memory.load"]).readings] == ["memory.load"]
    # L'ordre demande prime sur l'ordre des capteurs.
    assert [r.key for r in snapshot.filter(["memory.load", "cpu.load"]).readings] == [
        "memory.load",
        "cpu.load",
    ]


def test_filtrage_sans_selection_conserve_tout():
    snapshot = Snapshot.build([reading("cpu.load")])
    assert snapshot.filter(None) is snapshot
    assert snapshot.filter([]) is snapshot


def test_filtrage_ne_duplique_pas_une_mesure_captee_deux_fois():
    snapshot = Snapshot.build([reading("gpu.0.load", group=Group.GPU)])
    assert len(snapshot.filter(["gpu.*", "gpu.0.load"]).readings) == 1


def test_recherche_et_regroupement():
    snapshot = Snapshot.build([reading("cpu.load"), reading("gpu.0.load", group=Group.GPU)])
    assert snapshot.get("cpu.load").value == 50.0
    assert snapshot.get("absente") is None
    assert len(snapshot.by_group(Group.GPU)) == 1
