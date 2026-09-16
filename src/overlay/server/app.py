"""API HTTP/WebSocket : alimente l'application mobile et tout autre client."""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import AsyncIterator
from pathlib import Path
from typing import Any

from fastapi import Depends, FastAPI, HTTPException, Query, Request, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from overlay import __version__
from overlay.fps.tracker import FrameTimeTracker
from overlay.hub import MetricsHub
from overlay.server.auth import AuthThrottle, client_address, extract_token, token_matches

log = logging.getLogger(__name__)

WEBAPP_DIR = Path(__file__).resolve().parent.parent / "webapp"

#: Code de fermeture WebSocket « Policy Violation » : jeton absent ou invalide.
WS_POLICY_VIOLATION = 1008
#: Code de fermeture WebSocket « Try Again Later » : adresse temporairement verrouillee.
WS_TRY_LATER = 1013


class FramePayload(BaseModel):
    """Trame poussee par un jeu ou un script externe."""

    frame_time_ms: float | None = Field(default=None, gt=0, le=10_000)
    timestamp: float | None = None
    application: str | None = Field(default=None, max_length=120)


def create_app(
    hub: MetricsHub,
    *,
    token: str = "",
    tracker: FrameTimeTracker | None = None,
    metrics: list[str] | None = None,
    manage_hub: bool = True,
    throttle: AuthThrottle | None = None,
    trust_proxy: bool = False,
) -> FastAPI:
    """Construit l'application ASGI.

    `manage_hub` laisse le serveur demarrer et arreter le hub ; on le desactive
    quand l'overlay tourne dans le meme processus et pilote deja son cycle de vie.

    `throttle` verrouille une adresse apres des echecs d'authentification repetes,
    ce qui devient indispensable des que l'agent est joignable depuis Internet.
    `trust_proxy` autorise la lecture de `X-Forwarded-For` pour identifier le vrai
    client derriere un tunnel : a n'activer que si un proxy de confiance est en
    amont, cet en-tete etant trivial a forger autrement.
    """
    throttle = throttle or AuthThrottle()

    @contextlib.asynccontextmanager
    async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
        if manage_hub:
            await hub.start()
        try:
            yield
        finally:
            if manage_hub:
                await hub.stop()

    app = FastAPI(
        title="Overlay",
        version=__version__,
        description="Telemetrie materielle temps reel : FPS, temperatures, charges, ventilateurs.",
        lifespan=lifespan,
    )
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_methods=["GET", "POST"],
        allow_headers=["*"],
    )
    app.state.hub = hub
    app.state.token = token
    app.state.tracker = tracker
    app.state.metrics = metrics or []
    app.state.throttle = throttle

    def require_token(request: Request) -> None:
        client = client_address(
            (request.client.host, request.client.port) if request.client else None,
            dict(request.headers),
            trust_proxy,
        )
        if (attente := throttle.retry_after(client)) > 0:
            raise HTTPException(
                status_code=429,
                detail="Trop d'echecs d'authentification : reessayez plus tard",
                headers={"Retry-After": str(int(attente) + 1)},
            )
        presented = extract_token(dict(request.headers), dict(request.query_params))
        if not token_matches(token, presented):
            throttle.record_failure(client)
            raise HTTPException(status_code=401, detail="Jeton absent ou invalide")
        throttle.record_success(client)

    guarded = [Depends(require_token)]

    # --- Etat ----------------------------------------------------------------

    @app.get("/api/health")
    async def health() -> dict[str, Any]:
        """Sonde publique : permet a l'application mobile de trouver l'hote."""
        latest = hub.latest
        return {
            "service": "overlay",
            "version": __version__,
            "host": latest.host if latest else None,
            "auth_required": bool(token),
            "poll_interval": hub.poll_interval,
            "clients": hub.subscriber_count,
        }

    @app.get("/api/sensors", dependencies=guarded)
    async def sensors() -> dict[str, Any]:
        latest = hub.latest or await hub.poll()
        return {
            "backends": hub.describe_backends(),
            "metrics": [
                {
                    "key": reading.key,
                    "label": reading.label,
                    "unit": reading.unit,
                    "group": reading.group.value,
                    "kind": reading.kind.value,
                }
                for reading in latest.readings
            ],
        }

    @app.get("/api/metrics", dependencies=guarded)
    async def metrics_endpoint(
        keys: str | None = Query(default=None, description="Cles separees par des virgules"),
    ) -> dict[str, Any]:
        snapshot = hub.latest or await hub.poll()
        selection = _parse_keys(keys) or app.state.metrics
        return snapshot.filter(selection).to_dict()

    @app.get("/api/history", dependencies=guarded)
    async def history(
        limit: int = Query(default=60, ge=1, le=1000),
        keys: str | None = Query(default=None),
    ) -> dict[str, Any]:
        selection = _parse_keys(keys) or app.state.metrics
        return {
            "snapshots": [s.filter(selection).to_dict() for s in hub.history(limit)],
        }

    # --- Images par seconde --------------------------------------------------

    @app.post("/api/fps/frame", dependencies=guarded, status_code=202)
    async def push_frame(payload: FramePayload) -> dict[str, Any]:
        """Point d'entree pour un jeu ou un script qui mesure lui-meme ses trames."""
        if tracker is None:
            raise HTTPException(status_code=503, detail="Suivi FPS desactive")
        if payload.frame_time_ms is not None:
            tracker.add_frame_time(payload.frame_time_ms, application=payload.application)
        else:
            tracker.add_frame(payload.timestamp, application=payload.application)
        stats = tracker.stats()
        return {"fps": stats.fps, "frames": stats.frame_count}

    @app.get("/api/fps", dependencies=guarded)
    async def fps_state() -> dict[str, Any]:
        if tracker is None:
            raise HTTPException(status_code=503, detail="Suivi FPS desactive")
        stats = tracker.stats()
        return {
            "fps": stats.fps,
            "frame_time_ms": stats.frame_time_ms,
            "low_1_percent": stats.low_1_percent,
            "low_01_percent": stats.low_01_percent,
            "frames": stats.frame_count,
            "application": stats.application,
            "stale": stats.stale,
        }

    # --- Flux temps reel -----------------------------------------------------

    @app.websocket("/ws")
    async def stream(websocket: WebSocket) -> None:
        client = client_address(
            (websocket.client.host, websocket.client.port) if websocket.client else None,
            dict(websocket.headers),
            trust_proxy,
        )
        if throttle.retry_after(client) > 0:
            await websocket.close(code=WS_TRY_LATER, reason="Trop d'echecs")
            return
        presented = extract_token(dict(websocket.headers), dict(websocket.query_params))
        if not token_matches(token, presented):
            throttle.record_failure(client)
            # Refus avant acceptation : le navigateur voit un echec de handshake.
            await websocket.close(code=WS_POLICY_VIOLATION, reason="Jeton invalide")
            return
        throttle.record_success(client)
        await websocket.accept()

        selection = _parse_keys(websocket.query_params.get("keys")) or app.state.metrics
        queue = hub.subscribe()
        try:
            if hub.latest is not None:
                await websocket.send_json(hub.latest.filter(selection).to_dict())
            while True:
                snapshot = await queue.get()
                await websocket.send_json(snapshot.filter(selection).to_dict())
        except WebSocketDisconnect:
            pass
        except (asyncio.CancelledError, RuntimeError):
            # Coupure reseau brutale du telephone : rien a signaler.
            pass
        finally:
            hub.unsubscribe(queue)

    # --- Interface mobile ----------------------------------------------------

    if WEBAPP_DIR.is_dir():
        app.mount("/app", StaticFiles(directory=WEBAPP_DIR, html=True), name="webapp")

        @app.get("/", include_in_schema=False)
        async def index() -> FileResponse:
            return FileResponse(WEBAPP_DIR / "index.html")
    else:  # pragma: no cover - installation incomplete

        @app.get("/", include_in_schema=False)
        async def missing_webapp() -> JSONResponse:
            return JSONResponse({"detail": "Interface web absente du paquet"}, status_code=500)

    return app


def _parse_keys(raw: str | None) -> list[str]:
    if not raw:
        return []
    return [part.strip() for part in raw.split(",") if part.strip()]
