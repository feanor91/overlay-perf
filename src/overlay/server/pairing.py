"""Appairage du telephone : adresse joignable sur le reseau local et QR code."""

from __future__ import annotations

import ipaddress
import socket
from urllib.parse import quote


def local_ip_addresses() -> list[str]:
    """Adresses IPv4 par lesquelles le telephone peut joindre cette machine.

    La premiere est celle que l'OS utiliserait pour sortir sur le reseau : c'est
    presque toujours la bonne quand plusieurs interfaces coexistent (Wi-Fi, VPN,
    ponts Docker).
    """
    addresses: list[str] = []

    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        # Aucun paquet n'est emis : `connect` en UDP ne fait que choisir la route.
        probe.connect(("8.8.8.8", 80))
        addresses.append(probe.getsockname()[0])
    except OSError:
        pass
    finally:
        probe.close()

    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            candidate = info[4][0]
            if candidate not in addresses:
                addresses.append(candidate)
    except (socket.gaierror, OSError):
        pass

    def usable(address: str) -> bool:
        try:
            parsed = ipaddress.IPv4Address(address)
        except ipaddress.AddressValueError:
            return False
        return not (parsed.is_loopback or parsed.is_link_local or parsed.is_unspecified)

    filtered = [address for address in addresses if usable(address)]
    return filtered or ["127.0.0.1"]


def pairing_url(host: str, port: int, token: str = "", *, scheme: str = "http") -> str:
    """URL a ouvrir sur le telephone.

    Le jeton passe dans le fragment (`#token=`) : contrairement a la chaine de
    requete, un fragment n'est ni journalise par les serveurs ni transmis dans
    l'en-tete `Referer`.
    """
    base = f"{scheme}://{host}:{port}/"
    return with_token(base, token)


def with_token(base_url: str, token: str = "") -> str:
    """Ajoute le jeton en fragment a une URL deja complete (cas d'un tunnel)."""
    base = base_url if base_url.endswith("/") else f"{base_url}/"
    return f"{base}#token={quote(token, safe='')}" if token else base


def qr_matrix(text: str) -> list[list[bool]] | None:
    """Grille de modules noir/blanc du QR code, ou `None` si `qrcode` est absent.

    Partagee entre le rendu terminal (`render_qr`) et l'icone de zone de
    notification, qui la dessine elle-meme en pixels plutot qu'en demi-blocs.
    """
    try:
        import qrcode
    except ImportError:
        return None

    code = qrcode.QRCode(border=2)
    code.add_data(text)
    code.make(fit=True)
    return code.get_matrix()


def render_qr(text: str) -> str | None:
    """QR code en caracteres semi-graphiques, ou `None` si `qrcode` est absent."""
    matrix = qr_matrix(text)
    if matrix is None:
        return None

    # Deux lignes du QR par ligne de terminal grace aux demi-blocs Unicode.
    lines: list[str] = []
    for row in range(0, len(matrix), 2):
        top = matrix[row]
        bottom = matrix[row + 1] if row + 1 < len(matrix) else [False] * len(top)
        line = []
        for upper, lower in zip(top, bottom, strict=False):
            if upper and lower:
                line.append("█")
            elif upper:
                line.append("▀")
            elif lower:
                line.append("▄")
            else:
                line.append(" ")
        lines.append("".join(line))
    return "\n".join(lines)


def pairing_summary(
    port: int,
    token: str = "",
    *,
    public_url: str = "",
    scheme: str = "http",
) -> dict[str, object]:
    """Adresses d'appairage, locales et — si configuree — publique.

    Quand l'agent est joignable par un tunnel, c'est l'URL publique qui part dans
    le QR code : elle fonctionne aussi bien depuis la maison que depuis l'exterieur,
    alors que l'adresse locale ne vaut que sur place.
    """
    addresses = local_ip_addresses()
    local_urls = [pairing_url(address, port, token, scheme=scheme) for address in addresses]
    remote_url = with_token(public_url, token) if public_url else ""
    primary = remote_url or local_urls[0]
    return {
        "addresses": addresses,
        "urls": local_urls,
        "local_url": local_urls[0],
        "remote_url": remote_url,
        "primary_url": primary,
        "qr": render_qr(primary),
        "token": token,
    }
