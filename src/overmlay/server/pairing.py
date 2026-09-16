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


def pairing_url(host: str, port: int, token: str = "") -> str:
    """URL a ouvrir sur le telephone.

    Le jeton passe dans le fragment (`#token=`) : contrairement a la chaine de
    requete, un fragment n'est ni journalise par les serveurs ni transmis dans
    l'en-tete `Referer`.
    """
    base = f"http://{host}:{port}/"
    return f"{base}#token={quote(token, safe='')}" if token else base


def render_qr(text: str) -> str | None:
    """QR code en caracteres semi-graphiques, ou `None` si `qrcode` est absent."""
    try:
        import qrcode
    except ImportError:
        return None

    code = qrcode.QRCode(border=2)
    code.add_data(text)
    code.make(fit=True)
    matrix = code.get_matrix()

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


def pairing_summary(port: int, token: str = "") -> dict[str, object]:
    addresses = local_ip_addresses()
    primary = pairing_url(addresses[0], port, token)
    return {
        "addresses": addresses,
        "urls": [pairing_url(address, port, token) for address in addresses],
        "primary_url": primary,
        "qr": render_qr(primary),
        "token": token,
    }
