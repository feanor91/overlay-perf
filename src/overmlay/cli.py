"""Interface en ligne de commande d'Overmlay."""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import logging
import os
import sys
import threading
from pathlib import Path

from overmlay import __version__
from overmlay.config import Config, ConfigError, config_path, load_config
from overmlay.runtime import Runtime, build_runtime

log = logging.getLogger("overmlay")

#: Modele livre dans le paquet : reste accessible apres « pip install ».
EXEMPLE_CONFIG = Path(__file__).resolve().parent / "data" / "config.example.toml"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="overmlay",
        description="Overlay de monitoring materiel et serveur pour l'application mobile.",
    )
    parser.add_argument("--version", action="version", version=f"overmlay {__version__}")
    parser.add_argument("--config", type=Path, help="Chemin du fichier de configuration TOML")
    parser.add_argument("-v", "--verbose", action="count", default=0, help="Journaux detailles")

    sub = parser.add_subparsers(dest="command")

    run = sub.add_parser("run", help="Overlay + serveur mobile (defaut)")
    run.add_argument("--no-overlay", action="store_true", help="Ne pas afficher l'overlay")
    run.add_argument("--no-server", action="store_true", help="Ne pas demarrer le serveur")

    serve = sub.add_parser("serve", help="Serveur pour l'application mobile uniquement")
    serve.add_argument("--host", help="Adresse d'ecoute (defaut : 0.0.0.0)")
    serve.add_argument("--port", type=int, help="Port d'ecoute (defaut : 8777)")

    sub.add_parser("overlay", help="Overlay a l'ecran uniquement")

    sensors = sub.add_parser("sensors", help="Lister les capteurs detectes et un instantane")
    sensors.add_argument("--json", action="store_true", help="Sortie JSON")

    sub.add_parser("pair", help="Adresse et QR code a scanner depuis le telephone")

    config_cmd = sub.add_parser("config", help="Gerer le fichier de configuration")
    config_cmd.add_argument("--init", action="store_true", help="Creer un fichier d'exemple")
    config_cmd.add_argument("--path", action="store_true", help="Afficher le chemin attendu")

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    level = logging.WARNING - min(args.verbose, 2) * 10
    logging.basicConfig(level=level, format="%(asctime)s %(levelname)-7s %(name)s: %(message)s")

    if args.command == "config":
        return commande_config(args)

    try:
        config = load_config(args.config)
    except ConfigError as erreur:
        parser.error(str(erreur))
    except (OSError, ValueError) as erreur:
        print(f"Configuration illisible : {erreur}", file=sys.stderr)
        return 2

    commande = args.command or "run"
    if commande == "sensors":
        return commande_sensors(config, args)
    if commande == "pair":
        return commande_pair(config)
    if commande == "serve":
        return commande_run(config, overlay=False, server=True, args=args)
    if commande == "overlay":
        return commande_run(config, overlay=True, server=False, args=args)
    return commande_run(
        config,
        overlay=not getattr(args, "no_overlay", False),
        server=not getattr(args, "no_server", False),
        args=args,
    )


# --- Commandes -------------------------------------------------------------


def commande_config(args: argparse.Namespace) -> int:
    cible = args.config or config_path()
    if args.path or not args.init:
        print(cible)
        if not args.init:
            print("existe" if cible.is_file() else "absent (les valeurs par defaut s'appliquent)")
        return 0

    if cible.is_file():
        print(f"{cible} existe deja : rien n'a ete ecrit.", file=sys.stderr)
        return 1
    if not EXEMPLE_CONFIG.is_file():
        print("Modele de configuration introuvable dans le paquet.", file=sys.stderr)
        return 1
    cible.parent.mkdir(parents=True, exist_ok=True)
    cible.write_text(EXEMPLE_CONFIG.read_text(encoding="utf-8"), encoding="utf-8")
    print(f"Configuration creee : {cible}")
    return 0


def commande_sensors(config: Config, args: argparse.Namespace) -> int:
    runtime = build_runtime(config, need_token=False)
    snapshot = runtime.hub.poll_sync()
    # Deux lectures : les debits et la charge CPU sont des deltas entre deux appels.
    import time

    time.sleep(min(1.0, config.general.poll_interval))
    snapshot = runtime.hub.poll_sync()

    if args.json:
        import json

        print(
            json.dumps(
                {"backends": runtime.hub.describe_backends(), **snapshot.to_dict()},
                indent=2,
                ensure_ascii=False,
            )
        )
        return 0

    print(f"Machine : {snapshot.host}")
    print("Backends :")
    for backend in runtime.hub.describe_backends():
        etat = "actif" if backend["available"] else "indisponible"
        print(f"  - {backend['name']:<10} {etat:<13} {backend['description']}")

    if not snapshot.readings:
        print("\nAucune mesure disponible.")
        return 1

    print(f"\n{len(snapshot.readings)} mesures :")
    groupe_courant = None
    for reading in sorted(snapshot.readings, key=lambda r: (r.group.value, r.key)):
        if reading.group is not groupe_courant:
            groupe_courant = reading.group
            print(f"\n  [{groupe_courant.value}]")
        valeur = "—" if reading.value is None else f"{reading.value:g} {reading.unit}"
        print(f"    {reading.key:<40} {valeur:>16}   {reading.label}")
    return 0


def commande_pair(config: Config) -> int:
    from overmlay.config import resolve_token
    from overmlay.server.pairing import pairing_summary

    token = resolve_token(config)
    resume = pairing_summary(config.server.port, token)

    print("Ouvrez cette adresse sur le telephone, sur le meme reseau local :\n")
    print(f"  {resume['primary_url']}\n")
    autres = [url for url in resume["urls"] if url != resume["primary_url"]]
    if autres:
        print("Autres adresses possibles :")
        for url in autres:
            print(f"  {url}")
        print()
    if resume["qr"]:
        print(resume["qr"])
        print()
    else:
        print("(installez « qrcode » pour afficher un QR code a scanner)\n")
    print(f"Jeton : {token}")
    print("\nCe jeton donne acces a la telemetrie de la machine : ne le diffusez pas.")
    return 0


def commande_run(
    config: Config, *, overlay: bool, server: bool, args: argparse.Namespace
) -> int:
    if getattr(args, "host", None):
        config.server.host = args.host
    if getattr(args, "port", None):
        config.server.port = args.port
    if not config.server.enabled:
        server = False
    if not config.overlay.enabled:
        overlay = False
    if not overlay and not server:
        print("Rien a lancer : overlay et serveur sont tous deux desactives.", file=sys.stderr)
        return 1

    runtime = build_runtime(config, need_token=server)
    runtime.start_frame_source()
    try:
        if overlay:
            return _run_avec_overlay(runtime, server=server)
        return _run_serveur_seul(runtime)
    finally:
        runtime.stop_frame_source()


# --- Boucles d'execution ---------------------------------------------------


def _creer_serveur(runtime: Runtime):
    """Instancie le serveur uvicorn (import tardif : dependance optionnelle)."""
    try:
        import uvicorn

        from overmlay.server.app import create_app
    except ImportError as erreur:  # pragma: no cover - installation partielle
        raise SystemExit(
            "Le serveur requiert des dependances supplementaires : "
            "pip install 'overmlay[server]'"
        ) from erreur

    app = create_app(
        runtime.hub,
        token=runtime.token,
        tracker=runtime.tracker,
        metrics=runtime.config.server.metrics,
        manage_hub=False,  # le hub est demarre par la boucle appelante
    )
    configuration = uvicorn.Config(
        app,
        host=runtime.config.server.host,
        port=runtime.config.server.port,
        log_level="warning",
        access_log=False,
    )
    return uvicorn.Server(configuration)


def _annoncer_serveur(runtime: Runtime) -> None:
    from overmlay.server.pairing import local_ip_addresses, pairing_url

    adresse = local_ip_addresses()[0]
    port = runtime.config.server.port
    print(f"Serveur Overmlay sur http://{adresse}:{port}/")
    print(f"Telephone : {pairing_url(adresse, port, runtime.token)}")
    print("Astuce : « overmlay pair » affiche un QR code a scanner.")


def _run_serveur_seul(runtime: Runtime) -> int:
    async def principal() -> None:
        serveur = _creer_serveur(runtime)
        await runtime.hub.start()
        _annoncer_serveur(runtime)
        try:
            await serveur.serve()
        finally:
            await runtime.hub.stop()

    with contextlib.suppress(KeyboardInterrupt):  # interruption manuelle (Ctrl-C)
        asyncio.run(principal())
    return 0


def _run_avec_overlay(runtime: Runtime, *, server: bool) -> int:
    """Qt occupe le thread principal ; le hub et le serveur vivent dans un thread asyncio."""
    try:
        from PySide6.QtWidgets import QApplication

        from overmlay.overlay.hotkeys import HotkeyListener
        from overmlay.overlay.window import OverlayWindow, creer_minuterie
    except ImportError as erreur:
        raise SystemExit(
            "L'overlay requiert des dependances supplementaires : "
            "pip install 'overmlay[overlay]'"
        ) from erreur

    boucle_prete = threading.Event()
    conteneur: dict[str, object] = {}

    def thread_asyncio() -> None:
        async def principal() -> None:
            await runtime.hub.start()
            conteneur["loop"] = asyncio.get_running_loop()
            serveur = _creer_serveur(runtime) if server else None
            conteneur["serveur"] = serveur
            boucle_prete.set()
            try:
                if serveur is not None:
                    await serveur.serve()
                else:
                    await asyncio.Event().wait()  # maintient la collecte en vie
            finally:
                await runtime.hub.stop()

        try:
            asyncio.run(principal())
        except Exception:
            log.exception("Boucle de collecte interrompue")
        finally:
            boucle_prete.set()

    fil = threading.Thread(target=thread_asyncio, name="overmlay-async", daemon=True)
    fil.start()
    boucle_prete.wait(timeout=10.0)
    if server:
        _annoncer_serveur(runtime)

    application = QApplication.instance() or QApplication(sys.argv[:1])
    application.setQuitOnLastWindowClosed(False)  # masquer l'overlay ne doit pas quitter

    fenetre = OverlayWindow(runtime.config.overlay)
    intervalle = int(runtime.config.general.poll_interval * 1000)
    creer_minuterie(fenetre, lambda: runtime.hub.latest, max(100, intervalle // 2))

    raccourci = HotkeyListener(
        runtime.config.overlay.hotkey,
        fenetre.basculement_demande.emit,
    )
    raccourci_actif = raccourci.start()
    if runtime.config.overlay.visible_at_start:
        fenetre.show()
    elif not raccourci_actif:
        print(
            "Overlay masque au demarrage et raccourci indisponible : "
            "il ne pourra pas etre affiche. Activez « visible_at_start ».",
            file=sys.stderr,
        )
    if raccourci_actif:
        print(f"Raccourci d'affichage : {runtime.config.overlay.hotkey}")

    try:
        code = application.exec()
    except KeyboardInterrupt:  # pragma: no cover - interruption manuelle
        code = 0
    finally:
        raccourci.stop()
        serveur = conteneur.get("serveur")
        if serveur is not None:
            serveur.should_exit = True
        fil.join(timeout=5.0)
    return code


if __name__ == "__main__":  # pragma: no cover
    os.environ.setdefault("PYTHONUNBUFFERED", "1")
    raise SystemExit(main())
