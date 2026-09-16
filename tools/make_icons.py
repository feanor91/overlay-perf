"""Genere les icones PNG de la PWA sans dependance externe.

Encode directement un PNG RGBA (zlib + struct) : le depot reste installable avec
les seules dependances de production, et les icones se regenerent a l'identique.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

FOND = (11, 15, 23, 255)
BARRES = ((77, 163, 255, 255), (61, 220, 132, 255), (245, 181, 69, 255))
DESTINATION = Path(__file__).resolve().parent.parent / "src" / "overlay" / "webapp"


def png_bytes(width: int, height: int, pixels: list[list[tuple[int, int, int, int]]]) -> bytes:
    raw = bytearray()
    for row in pixels:
        raw.append(0)  # filtre 0 (aucun) pour chaque scanline
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + tag
            + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def draw(size: int, *, rounded: bool) -> list[list[tuple[int, int, int, int]]]:
    """Dessine trois barres croissantes, metaphore d'un graphe de performance."""
    pixels = [[FOND for _ in range(size)] for _ in range(size)]
    radius = size * 0.18 if rounded else 0.0

    # Les icones « maskable » sont rognees en cercle par Android : on garde une
    # zone de securite de 20 % sur chaque bord.
    inset = size * 0.28 if not rounded else size * 0.20
    usable = size - 2 * inset
    bar_width = usable / 5.0
    gap = bar_width / 2.0
    heights = (0.42, 0.68, 1.0)

    for y in range(size):
        for x in range(size):
            if rounded and not _inside_rounded(x, y, size, radius):
                pixels[y][x] = (0, 0, 0, 0)
                continue
            for index, (colour, factor) in enumerate(zip(BARRES, heights, strict=True)):
                left = inset + index * (bar_width + gap)
                right = left + bar_width
                top = inset + usable * (1.0 - factor)
                bottom = size - inset
                if left <= x < right and top <= y < bottom:
                    pixels[y][x] = colour
    return pixels


def _inside_rounded(x: int, y: int, size: int, radius: float) -> bool:
    if radius <= 0:
        return True
    coins = (
        (radius, radius),
        (size - radius, radius),
        (radius, size - radius),
        (size - radius, size - radius),
    )
    for corner_x, corner_y in coins:
        gauche = corner_x == radius
        haut = corner_y == radius
        outside_x = (x < radius) if gauche else (x >= size - radius)
        outside_y = (y < radius) if haut else (y >= size - radius)
        if outside_x and outside_y:
            return (x + 0.5 - corner_x) ** 2 + (y + 0.5 - corner_y) ** 2 <= radius**2
    return True


def main() -> None:
    DESTINATION.mkdir(parents=True, exist_ok=True)
    for name, size, rounded in (
        ("icon-192.png", 192, True),
        ("icon-512.png", 512, True),
        ("icon-maskable.png", 512, False),
    ):
        target = DESTINATION / name
        target.write_bytes(png_bytes(size, size, draw(size, rounded=rounded)))
        print(f"{target.relative_to(DESTINATION.parents[3])} : {target.stat().st_size} octets")


if __name__ == "__main__":
    main()
