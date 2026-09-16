"""Authentification par jeton partage.

Un jeton est exige sur chaque route, y compris le WebSocket. Il peut arriver via
l'en-tete `Authorization: Bearer ...`, l'en-tete `X-Overmlay-Token` ou le parametre
d'URL `token` (seul moyen d'authentifier un WebSocket depuis un navigateur, qui ne
permet pas d'ajouter d'en-tete a la poignee de main).

Des que l'agent devient joignable au-dela du reseau local, un jeton seul ne suffit
plus : n'importe qui peut essayer des millions de combinaisons. `AuthThrottle`
verrouille donc une adresse apres quelques echecs, ce qui ramene une attaque par
force brute a une duree sans interet pratique.
"""

from __future__ import annotations

import hmac
import threading
import time

TOKEN_QUERY_PARAM = "token"
TOKEN_HEADER = "x-overmlay-token"


def extract_token(headers: dict[str, str], query: dict[str, str]) -> str | None:
    """Recupere le jeton presente par le client, quel que soit son emplacement."""
    lowered = {key.lower(): value for key, value in headers.items()}
    authorization = lowered.get("authorization", "")
    if authorization.lower().startswith("bearer "):
        return authorization[7:].strip() or None
    header_token = lowered.get(TOKEN_HEADER)
    if header_token:
        return header_token.strip() or None
    query_token = query.get(TOKEN_QUERY_PARAM)
    return query_token.strip() if query_token else None


def token_matches(expected: str, presented: str | None) -> bool:
    """Comparaison a temps constant, pour ne pas fuiter le jeton octet par octet."""
    if not expected:
        return True  # jeton desactive explicitement dans la configuration
    if not presented:
        return False
    return hmac.compare_digest(expected, presented)


class AuthThrottle:
    """Verrouillage par adresse apres des echecs d'authentification repetes.

    Les echecs sont comptes sur une fenetre glissante : quelques erreurs de frappe
    espacees dans le temps ne verrouillent jamais, une salve automatisee si.
    """

    def __init__(
        self,
        *,
        max_failures: int = 10,
        window: float = 300.0,
        lockout: float = 300.0,
        max_tracked: int = 4096,
    ) -> None:
        self.max_failures = max_failures
        self.window = window
        self.lockout = lockout
        self.max_tracked = max_tracked
        self._lock = threading.Lock()
        # Adresse -> (horodatages des echecs recents, fin du verrouillage).
        self._failures: dict[str, list[float]] = {}
        self._locked_until: dict[str, float] = {}

    def retry_after(self, client: str | None) -> float:
        """Secondes restantes avant de pouvoir reessayer ; 0 si l'acces est ouvert."""
        if not client:
            return 0.0
        now = time.monotonic()
        with self._lock:
            fin = self._locked_until.get(client)
            if fin is None:
                return 0.0
            if fin <= now:
                del self._locked_until[client]
                self._failures.pop(client, None)
                return 0.0
            return fin - now

    def record_failure(self, client: str | None) -> None:
        if not client:
            return
        now = time.monotonic()
        with self._lock:
            self._prune(now)
            recents = [t for t in self._failures.get(client, []) if now - t < self.window]
            recents.append(now)
            self._failures[client] = recents
            if len(recents) >= self.max_failures:
                self._locked_until[client] = now + self.lockout
                self._failures[client] = []

    def record_success(self, client: str | None) -> None:
        """Une authentification reussie efface l'ardoise de cette adresse."""
        if not client:
            return
        with self._lock:
            self._failures.pop(client, None)
            self._locked_until.pop(client, None)

    def _prune(self, now: float) -> None:
        """Empeche la table de grossir indefiniment sous un balayage d'adresses."""
        for client, fin in list(self._locked_until.items()):
            if fin <= now:
                del self._locked_until[client]
        for client, horodatages in list(self._failures.items()):
            if all(now - t >= self.window for t in horodatages):
                del self._failures[client]
        if len(self._failures) > self.max_tracked:
            surplus = len(self._failures) - self.max_tracked
            for client in sorted(self._failures, key=lambda c: max(self._failures[c]))[:surplus]:
                del self._failures[client]


def client_address(scope_client: tuple[str, int] | None, headers: dict[str, str],
                   trust_proxy: bool) -> str | None:
    """Adresse du client, en tenant compte d'un eventuel tunnel en amont.

    `X-Forwarded-For` n'est lu que si l'agent est explicitement declare derriere un
    proxy de confiance : sinon n'importe qui pourrait forger cet en-tete et echapper
    au verrouillage en changeant de valeur a chaque essai.
    """
    if trust_proxy:
        lowered = {key.lower(): value for key, value in headers.items()}
        transmis = lowered.get("x-forwarded-for", "")
        if transmis:
            premier = transmis.split(",")[0].strip()
            if premier:
                return premier
    return scope_client[0] if scope_client else None
