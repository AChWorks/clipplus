#!/usr/bin/env python3
"""Generate a DPI-aware Windows ICO from ClipPlus's simple geometric SVG design.
No third-party packages required. Run from any directory."""
from pathlib import Path
import math
import struct
import zlib

OUTPUT = Path(__file__).resolve().parents[1] / "src/ClipPlus/Assets/ClipPlus.ico"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
BLUE = (37, 99, 235, 255)
WHITE = (255, 255, 255, 255)
CLEAR = (0, 0, 0, 0)


def rounded_distance(x, y, cx, cy, w, h, r):
    dx, dy = abs(x - cx) - w / 2 + r, abs(y - cy) - h / 2 + r
    return math.hypot(max(dx, 0), max(dy, 0)) + min(max(dx, dy), 0) - r


def pixel(x, y):
    if rounded_distance(x, y, 50, 50, 90, 90, 21) > 0:
        return CLEAR
    frame = abs(rounded_distance(x, y, 50, 54, 48, 58, 7)) <= 2.5
    clip = rounded_distance(x, y, 50, 25, 26, 16, 5) <= 0
    vertical = math.hypot(x - 50, y - min(68, max(44, y))) <= 3
    horizontal = math.hypot(x - min(62, max(38, x)), y - 56) <= 3
    return WHITE if frame or clip or vertical or horizontal else BLUE


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def image(size):
    sampling = 4
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            rgba = [0, 0, 0, 0]
            for sy in range(sampling):
                for sx in range(sampling):
                    point = pixel((x + (sx + .5) / sampling) * 100 / size,
                                  (y + (sy + .5) / sampling) * 100 / size)
                    rgba[3] += point[3]
                    for c in range(3):
                        rgba[c] += point[c] * point[3]
            alpha = rgba[3]
            raw.extend([round(rgba[c] / alpha) if alpha else 0 for c in range(3)])
            raw.append(round(alpha / (sampling * sampling)))
    return (b"\x89PNG\r\n\x1a\n" +
            png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) +
            png_chunk(b"IDAT", zlib.compress(bytes(raw), 9)) +
            png_chunk(b"IEND", b""))


def main():
    entries, frames = [], []
    offset = 6 + 16 * len(SIZES)
    for size in SIZES:
        data = image(size)
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        frames.append(data)
        offset += len(data)
    OUTPUT.write_bytes(struct.pack("<HHH", 0, 1, len(SIZES)) + b"".join(entries + frames))
    print(f"{OUTPUT}: {OUTPUT.stat().st_size} bytes, {len(SIZES)} icon sizes")


if __name__ == "__main__":
    main()
