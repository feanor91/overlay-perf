#!/usr/bin/env python3
"""Exemple : alimenter le compteur FPS d'Overmlay depuis son propre programme.

Utile quand ni PresentMon ni MangoHud ne conviennent : un moteur de jeu, un
emulateur ou un banc de test peut publier ses trames sur l'API HTTP.

Mettre `mode = "push"` dans la section [fps] de la configuration, puis :

    python examples/push_fps.py --url http://127.0.0.1:8777 --token VOTRE_JETON
"""

from __future__ import annotations

import argparse
import json
import time
import urllib.error
import urllib.request


def push_frame(base_url: str, token: str, frame_time_ms: float, application: str) -> None:
    """Publie une duree de trame. Une trame perdue n'est jamais rejouee."""
    requete = urllib.request.Request(
        f"{base_url.rstrip('/')}/api/fps/frame",
        data=json.dumps(
            {"frame_time_ms": frame_time_ms, "application": application}
        ).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
        },
        method="POST",
    )
    with urllib.request.urlopen(requete, timeout=2.0) as reponse:
        reponse.read()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8777")
    parser.add_argument("--token", required=True)
    parser.add_argument("--fps", type=float, default=60.0, help="Cadence a simuler")
    parser.add_argument("--application", default="demo.py")
    parser.add_argument("--duration", type=float, default=30.0, help="Duree en secondes")
    args = parser.parse_args()

    periode = 1.0 / args.fps
    fin = time.monotonic() + args.duration
    precedent = time.perf_counter()
    envoyees = 0

    while time.monotonic() < fin:
        time.sleep(periode)
        maintenant = time.perf_counter()
        duree_ms = (maintenant - precedent) * 1000.0
        precedent = maintenant
        try:
            push_frame(args.url, args.token, duree_ms, args.application)
        except urllib.error.HTTPError as erreur:
            print(f"Refus du serveur : {erreur.code} {erreur.reason}")
            return 1
        except OSError as erreur:
            print(f"Agent injoignable : {erreur}")
            return 1
        envoyees += 1

    print(f"{envoyees} trames publiees.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
