#!/usr/bin/env python3
"""Generate the native multi-size ICO and WPF drawing from ClipPlus.svg.

The small product icon uses only rounded rectangles and round-capped lines.
Keep those primitives in the SVG as the single editable design source.
No third-party packages or runtime SVG renderer required.
"""
from pathlib import Path
import math
import struct
import xml.etree.ElementTree as ET
import zlib

ASSETS = Path(__file__).resolve().parents[1] / "src/ClipPlus/Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
CLEAR = (0, 0, 0, 0)
SVG_NS = "{http://www.w3.org/2000/svg}"


def rgba(value):
    return tuple(bytes.fromhex(value.removeprefix("#"))) + (255,) if value != "none" else CLEAR


def rounded_distance(x, y, cx, cy, w, h, r):
    dx, dy = abs(x - cx) - w / 2 + r, abs(y - cy) - h / 2 + r
    return math.hypot(max(dx, 0), max(dy, 0)) + min(max(dx, dy), 0) - r


def segment_distance(x, y, x1, y1, x2, y2):
    dx, dy = x2 - x1, y2 - y1
    t = min(1, max(0, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (x1 + t * dx), y - (y1 + t * dy))


def read_design():
    root = ET.parse(ASSETS / "ClipPlus.svg").getroot()
    x, y, width, height = map(float, root.attrib["viewBox"].split())
    if x != 0 or y != 0 or width <= 0 or width != height:
        raise ValueError("The app icon needs a positive square viewBox starting at zero.")
    shapes = []
    for node in root:
        kind = node.tag.removeprefix(SVG_NS)
        a = node.attrib
        fill = a.get("fill", "none")
        stroke = a.get("stroke", "none")
        thickness = float(a.get("stroke-width", "0"))
        if kind == "rect":
            geometry = tuple(float(a[k]) for k in ("x", "y", "width", "height", "rx"))
        elif kind == "line" and a.get("stroke-linecap") == "round":
            geometry = tuple(float(a[k]) for k in ("x1", "y1", "x2", "y2"))
        else:
            raise ValueError(f"Unsupported icon design primitive: {kind}")
        shapes.append((kind, geometry, fill, stroke, thickness))
    return width, shapes


VIEW, SHAPES = read_design()
RASTER_SHAPES = [
    (kind,
     (g[0] + g[2] / 2, g[1] + g[3] / 2, g[2], g[3], g[4]) if kind == "rect" else g,
     rgba(fill), rgba(stroke), thickness / 2)
    for kind, g, fill, stroke, thickness in SHAPES
]


def pixel(x, y):
    color = CLEAR
    for kind, geometry, fill, stroke, radius in RASTER_SHAPES:
        if kind == "rect":
            distance = rounded_distance(x, y, *geometry)
            if fill[3] and distance <= 0:
                color = fill
            if stroke[3] and abs(distance) <= radius:
                color = stroke
        elif segment_distance(x, y, *geometry) <= radius:
            color = stroke
    return color


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def image(size):
    sampling = 4
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            rgba_sum = [0, 0, 0, 0]
            for sy in range(sampling):
                for sx in range(sampling):
                    point = pixel((x + (sx + .5) / sampling) * VIEW / size,
                                  (y + (sy + .5) / sampling) * VIEW / size)
                    rgba_sum[3] += point[3]
                    for c in range(3):
                        rgba_sum[c] += point[c] * point[3]
            alpha = rgba_sum[3]
            raw.extend([round(rgba_sum[c] / alpha) if alpha else 0 for c in range(3)])
            raw.append(round(alpha / (sampling * sampling)))
    return (b"\x89PNG\r\n\x1a\n" +
            png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) +
            png_chunk(b"IDAT", zlib.compress(bytes(raw), 9)) +
            png_chunk(b"IEND", b""))


def write_drawing():
    # Preserve the SVG's full square viewport, including transparent padding.
    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        '    <!-- Generated from ClipPlus.svg by scripts/Generate-Icon.py. -->',
        '    <DrawingImage x:Key="ClipPlusHeaderIcon">',
        '        <DrawingImage.Drawing>',
        '            <DrawingGroup>',
        '                <GeometryDrawing Brush="Transparent">',
        '                    <GeometryDrawing.Geometry>',
        f'                        <RectangleGeometry Rect="0,0,{VIEW:g},{VIEW:g}"/>',
        '                    </GeometryDrawing.Geometry>',
        '                </GeometryDrawing>',
    ]
    for kind, g, fill, stroke, thickness in SHAPES:
        brush = f' Brush="{fill}"' if fill != "none" else ""
        lines.append(f'                <GeometryDrawing{brush}>')
        lines.append('                    <GeometryDrawing.Geometry>')
        if kind == "rect":
            x, y, w, h, r = g
            lines.append(f'                        <RectangleGeometry Rect="{x:g},{y:g},{w:g},{h:g}" RadiusX="{r:g}" RadiusY="{r:g}"/>')
        else:
            x1, y1, x2, y2 = g
            lines.append(f'                        <LineGeometry StartPoint="{x1:g},{y1:g}" EndPoint="{x2:g},{y2:g}"/>')
        lines.append('                    </GeometryDrawing.Geometry>')
        if stroke != "none":
            lines.extend([
                '                    <GeometryDrawing.Pen>',
                f'                        <Pen Brush="{stroke}" Thickness="{thickness:g}" StartLineCap="Round" EndLineCap="Round"/>',
                '                    </GeometryDrawing.Pen>',
            ])
        lines.append('                </GeometryDrawing>')
    lines.extend(['            </DrawingGroup>', '        </DrawingImage.Drawing>',
                  '    </DrawingImage>', '</ResourceDictionary>'])
    (ASSETS / "ClipPlusIcon.xaml").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    entries, frames = [], []
    offset = 6 + 16 * len(SIZES)
    for size in SIZES:
        data = image(size)
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        frames.append(data)
        offset += len(data)
    output = ASSETS / "ClipPlus.ico"
    output.write_bytes(struct.pack("<HHH", 0, 1, len(SIZES)) + b"".join(entries + frames))
    write_drawing()
    print(f"{output}: {output.stat().st_size} bytes, {len(SIZES)} icon sizes; WPF drawing generated.")


if __name__ == "__main__":
    main()
