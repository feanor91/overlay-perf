"""Greffon pytest simulant une horloge monotone a faible resolution.

Sous Windows, avant Python 3.13, `time.monotonic()` repose sur `GetTickCount64` et
avance par pas d'environ 15,6 ms : deux appels rapproches renvoient la meme valeur.
Tout code qui deduit une grandeur d'un ecart de temps — debits, peremption d'un flux
— peut se comporter differemment la-bas, et la CI ne le revele qu'apres coup.

Ce greffon quantifie l'horloge de la meme facon, ce qui rend le probleme
reproductible sur n'importe quelle machine :

    python -m pytest -q -p tests.coarse_clock
"""

from __future__ import annotations

import time

#: Pas de l'horloge Windows historique : 1/64 de seconde.
PAS_SECONDES = 0.015625

_monotonic_reel = time.monotonic


def _monotonic_quantifie() -> float:
    return (_monotonic_reel() // PAS_SECONDES) * PAS_SECONDES


time.monotonic = _monotonic_quantifie


def pytest_report_header() -> str:
    return f"horloge monotone quantifiee a {PAS_SECONDES * 1000:.4g} ms (simulation Windows)"
