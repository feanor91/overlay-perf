import asyncio

import pytest

from overmlay.hub import MetricsHub, merge_readings
from overmlay.models import Group, Snapshot
from tests.helpers import BrokenBackend, StaticBackend, reading


def test_fusion_donne_la_priorite_au_premier_backend():
    prioritaire = [reading("gpu.0.temp", 61.0), reading("cpu.load", 10.0)]
    secondaire = [reading("gpu.0.temp", 99.0), reading("memory.load", 40.0)]
    fusion = {r.key: r.value for r in merge_readings([prioritaire, secondaire])}
    assert fusion == {"gpu.0.temp": 61.0, "cpu.load": 10.0, "memory.load": 40.0}


def test_fusion_conserve_l_ordre():
    resultat = merge_readings([[reading("b")], [reading("a")]])
    assert [r.key for r in resultat] == ["b", "a"]


def test_cadence_invalide_refusee():
    with pytest.raises(ValueError):
        MetricsHub([], poll_interval=0)


def test_lecture_synchrone():
    hub = MetricsHub([StaticBackend([reading("cpu.load", 33.0)])])
    snapshot = hub.poll_sync()
    assert snapshot.get("cpu.load").value == 33.0
    assert hub.latest is snapshot


def test_un_backend_casse_n_empeche_pas_les_autres():
    hub = MetricsHub([BrokenBackend(), StaticBackend([reading("cpu.load", 12.0)])])
    assert hub.poll_sync().get("cpu.load").value == 12.0


async def test_boucle_de_collecte_et_diffusion():
    hub = MetricsHub([StaticBackend([reading("cpu.load", 55.0)])], poll_interval=0.05)
    async with hub:
        queue = hub.subscribe()
        assert hub.subscriber_count == 1
        snapshot = await asyncio.wait_for(queue.get(), timeout=2.0)
        assert snapshot.get("cpu.load").value == 55.0
        hub.unsubscribe(queue)
        assert hub.subscriber_count == 0


async def test_historique_borne():
    hub = MetricsHub([StaticBackend([reading("cpu.load")])], poll_interval=0.02, history_size=3)
    async with hub:
        await asyncio.sleep(0.25)
    assert len(hub.history()) == 3
    assert len(hub.history(limit=2)) == 2


async def test_abonne_lent_recoit_les_mesures_les_plus_recentes():
    """Un telephone sature ne doit pas bloquer la collecte ni recevoir du perime.

    La diffusion est sollicitee directement : compter sur la boucle d'echantillonnage
    rendrait le resultat dependant de la resolution de l'horloge, qui varie fortement
    d'un systeme a l'autre.
    """
    hub = MetricsHub([StaticBackend([])], poll_interval=1.0)
    queue = hub.subscribe(maxsize=2)

    for valeur in range(1, 6):
        hub._publish(Snapshot.build([reading("cpu.load", float(valeur))]))

    assert queue.qsize() == 2
    retenues = [(await queue.get()).get("cpu.load").value for _ in range(2)]
    # Ce sont les deux dernieres emises, pas les deux premieres.
    assert retenues == [4.0, 5.0]


async def test_la_collecte_continue_malgre_un_abonne_sature():
    """Une file pleine ne doit jamais bloquer la boucle d'echantillonnage."""
    hub = MetricsHub([StaticBackend([reading("cpu.load", 7.0)])], poll_interval=0.02)
    async with hub:
        hub.subscribe(maxsize=1)  # abonne qui ne consomme jamais
        await asyncio.sleep(0.2)
    assert len(hub.history()) >= 2


async def test_arret_ferme_les_backends():
    ferme = []

    class Fermable(StaticBackend):
        def close(self):
            ferme.append(self.name)

    hub = MetricsHub([Fermable([reading("cpu.load")], name="a")], poll_interval=0.05)
    await hub.start()
    await hub.stop()
    assert ferme == ["a"]


async def test_double_demarrage_sans_effet():
    hub = MetricsHub([StaticBackend([reading("cpu.load")])], poll_interval=0.05)
    async with hub:
        tache = hub._task
        await hub.start()
        assert hub._task is tache


def test_description_des_backends():
    hub = MetricsHub([StaticBackend([], name="alpha")])
    assert hub.describe_backends() == [
        {"name": "alpha", "description": "", "available": True}
    ]


def test_les_groupes_sont_preserves():
    hub = MetricsHub([StaticBackend([reading("gpu.0.load", group=Group.GPU)])])
    assert hub.poll_sync().by_group(Group.GPU)[0].key == "gpu.0.load"
