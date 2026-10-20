"""Generate src/Clotch.App/Assets/clotch.ico (16..256 px) with the Python stdlib only.

Design: dark rounded square, terracotta "notch" pill hanging from the top edge,
and two usage bars underneath. Rendered with 4x supersampling for anti-aliasing.
Run: python tools/make_icon.py
"""
import os
import struct
import zlib

BG = (31, 30, 29)
ORANGE = (217, 119, 87)
LIGHT = (240, 238, 230)
TRACK = (70, 68, 64)


def rounded_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def shade(u, v):
    """Colour of unit-square point (u, v) or None for transparent."""
    if not rounded_rect(u, v, 0.0, 0.0, 1.0, 1.0, 0.22):
        return None
    # notch pill hanging from top
    if rounded_rect(u, v, 0.22, -0.2, 0.78, 0.30, 0.14):
        return ORANGE
    # bar 1 (session): track + 70% fill
    if rounded_rect(u, v, 0.16, 0.46, 0.84, 0.60, 0.07):
        return LIGHT if u <= 0.16 + 0.68 * 0.70 else TRACK
    # bar 2 (weekly): track + 40% fill
    if rounded_rect(u, v, 0.16, 0.70, 0.84, 0.84, 0.07):
        return ORANGE if u <= 0.16 + 0.68 * 0.40 else TRACK
    return BG


def render(size, ss=4):
    rows = []
    n = size * ss
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    c = shade((px * ss + sx + 0.5) / n, (py * ss + sy + 0.5) / n)
                    if c is not None:
                        acc[0] += c[0]
                        acc[1] += c[1]
                        acc[2] += c[2]
                        acc[3] += 1
            k = ss * ss
            a = acc[3]
            if a:
                row += bytes((acc[0] // a, acc[1] // a, acc[2] // a, 255 * a // k))
            else:
                row += b"\0\0\0\0"
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\0" + r for r in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    images = [png(s, render(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out = os.path.join(os.path.dirname(__file__), "..", "src", "Clotch.App", "Assets", "clotch.ico")
    with open(out, "wb") as f:
        f.write(header + entries + b"".join(images))
    with open(os.path.join(os.path.dirname(out), "clotch-256.png"), "wb") as f:
        f.write(images[-1])
    print("wrote", os.path.abspath(out))


if __name__ == "__main__":
    main()
