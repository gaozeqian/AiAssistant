#!/usr/bin/env python3
"""Generates the AiAssistant app icon (PNG + ICO) without external imaging libraries.

The icon is a rounded square with the product accent gradient and a clock glyph,
matching the desktop shell's visual identity. Run from the repository root:

    python build/package/assets/generate-icon.py
"""

import struct
import zlib
from pathlib import Path

SIZE = 256
ACCENT = (49, 87, 213)
DARK = (17, 28, 54)
WHITE = (255, 255, 255)


def rounded_alpha(x: float, y: float, radius: float = 48.0) -> float:
    """Anti-aliased coverage for a rounded square of SIZE x SIZE."""
    cx = min(max(x, radius), SIZE - radius)
    cy = min(max(y, radius), SIZE - radius)
    dx = x - cx
    dy = y - cy
    distance = (dx * dx + dy * dy) ** 0.5
    if x < 0 or y < 0 or x > SIZE or y > SIZE:
        return 0.0
    return max(0.0, min(1.0, radius - distance + 0.5))


def clock_coverage(x: float, y: float) -> float:
    """Ring plus two hands, centered at (128, 128)."""
    cx = cy = SIZE / 2
    dx = x - cx
    dy = y - cy
    distance = (dx * dx + dy * dy) ** 0.5
    # outer ring
    if 78.0 <= distance <= 88.0:
        return 1.0
    # minute hand (up)
    if abs(dx) <= 4.0 and -66.0 <= dy <= 0.0:
        return 1.0
    # hour hand (right)
    if abs(dy) <= 5.0 and 0.0 <= dx <= 44.0:
        return 1.0
    # center dot
    if distance <= 8.0:
        return 1.0
    return 0.0


def render(size: int = SIZE) -> bytes:
    scale = SIZE / size
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            x = (px + 0.5) * scale
            y = (py + 0.5) * scale
            alpha = rounded_alpha(x, y)
            if alpha <= 0.0:
                row.extend((0, 0, 0, 0))
                continue
            t = (x + y) / (2 * SIZE)
            base = tuple(int(ACCENT[i] * (1 - t) + DARK[i] * t) for i in range(3))
            glyph = clock_coverage(x, y)
            color = tuple(int(base[i] * (1 - glyph) + WHITE[i] * glyph) for i in range(3))
            row.extend((color[0], color[1], color[2], int(alpha * 255)))
        rows.append(bytes(row))
    raw = b"".join(b"\x00" + row for row in rows)
    compressed = zlib.compress(raw, 9)

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", compressed) + chunk(b"IEND", b"")


def render_wide(width: int, height: int) -> bytes:
    """Letterboxes the square icon onto a transparent wide canvas for MSIX tiles."""
    scale = SIZE / height
    rows = []
    left_pad = (width - height) // 2
    for py in range(height):
        row = bytearray(b"\x00" * (left_pad * 4))
        # Re-use the square row by re-rendering only the visible columns.
        for px in range(height):
            x = (px + 0.5) * scale
            y = (py + 0.5) * scale
            alpha = rounded_alpha(x, y)
            if alpha <= 0.0:
                row.extend((0, 0, 0, 0))
                continue
            t = (x + y) / (2 * SIZE)
            base = tuple(int(ACCENT[i] * (1 - t) + DARK[i] * t) for i in range(3))
            glyph = clock_coverage(x, y)
            color = tuple(int(base[i] * (1 - glyph) + WHITE[i] * glyph) for i in range(3))
            row.extend((color[0], color[1], color[2], int(alpha * 255)))
        row.extend(b"\x00" * ((width - height - left_pad) * 4))
        rows.append(bytes(row))
    raw = b"".join(rows)
    compressed = zlib.compress(raw, 9)

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", compressed) + chunk(b"IEND", b"")


def main() -> None:
    out_dir = Path(__file__).resolve().parent
    png = render(SIZE)
    (out_dir / "aiassistant.png").write_bytes(png)

    # ICO container with PNG-compressed entries (supported since Windows Vista).
    sizes = [32, 64, 128, 256]
    images = [render(size) for size in sizes]
    offset = 6 + 16 * len(images)
    entries = b""
    for size, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    ico = struct.pack("<HHH", 0, 1, len(images)) + entries + b"".join(images)
    (out_dir / "aiassistant.ico").write_bytes(ico)

    # MSIX tile assets (generated only when the Windows package layout is requested).
    msix_dir = out_dir / "msix"
    msix_dir.mkdir(exist_ok=True)
    for name, size in [
        ("StoreLogo.png", 50),
        ("Square44x44Logo.png", 44),
        ("Square150x150Logo.png", 150),
    ]:
        (msix_dir / name).write_bytes(render(size))
    (msix_dir / "Wide310x150Logo.png").write_bytes(render_wide(310, 150))
    (msix_dir / "SplashScreen.png").write_bytes(render_wide(620, 300))
    print(f"wrote icon assets under {out_dir}")


if __name__ == "__main__":
    main()
