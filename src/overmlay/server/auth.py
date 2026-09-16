"""Authentification par jeton partage pour l'API locale.

Le serveur ecoute sur le reseau local pour que le telephone puisse s'y connecter :
un jeton est donc exige sur chaque route, y compris le WebSocket. Il peut arriver
via l'en-tete `Authorization: Bearer ...`, l'en-tete `X-Overmlay-Token` ou le
parametre d'URL `token` (seul moyen d'authentifier un WebSocket depuis un
navigateur, qui ne permet pas d'ajouter d'en-tete a la poignee de main).
"""

from __future__ import annotations

import hmac

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
