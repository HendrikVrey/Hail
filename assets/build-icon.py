"""Draws Hail's icon from its geometry and writes hail.ico and hail.png.

The mark: a four-pointed spark on a rounded plate. Every size is drawn from the geometry on
its own (eight times supersampled, reduced once) rather than shrunk from the largest, which is
what keeps the spark a spark at 16 px. The ICO is written by hand as PNG payloads, one per
size, so each frame is exactly the picture drawn for it.

M0 placeholder, to be judged by eye like Sling's was. Pillow only:

    python assets/build-icon.py
"""

import io
import math
import pathlib
import struct

from PIL import Image, ImageDraw

HERE = pathlib.Path(__file__).parent
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SUPERSAMPLE = 8

PLATE_TOP = (124, 92, 255)      # violet, top left
PLATE_BOTTOM = (40, 140, 255)   # azure, bottom right
SPARK = (255, 255, 255)

CORNER = 0.22        # plate corner radius, as a fraction of the size
SPARK_RADIUS = 0.36  # tip distance from the centre
SPARK_POWER = 3.2    # 3 is an astroid; lower is a fuller spark


def plate(size):
    """The rounded square, filled with a diagonal gradient."""
    gradient = Image.new("RGB", (size, size))
    pixels = gradient.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(PLATE_TOP, PLATE_BOTTOM))

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=CORNER * size, fill=255)

    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(gradient, (0, 0), mask)
    return out


def spark_points(size):
    """A four-pointed star with concave sides: a superellipse with an exponent below one."""
    centre = size / 2
    radius = SPARK_RADIUS * size
    points = []
    for i in range(720):
        t = 2 * math.pi * i / 720
        c, s = math.cos(t), math.sin(t)
        x = math.copysign(abs(c) ** SPARK_POWER, c)
        y = math.copysign(abs(s) ** SPARK_POWER, s)
        points.append((centre + radius * x, centre + radius * y))
    return points


def draw(size):
    big = size * SUPERSAMPLE
    image = plate(big)
    ImageDraw.Draw(image).polygon(spark_points(big), fill=SPARK + (255,))
    return image.resize((size, size), Image.LANCZOS)


def write_ico(frames, path):
    """ICONDIR, one ICONDIRENTRY per frame, then the PNG payloads."""
    payloads = []
    for frame in frames:
        buffer = io.BytesIO()
        frame.save(buffer, format="PNG")
        payloads.append(buffer.getvalue())

    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = len(header) + 16 * len(frames)
    entries = b""
    for frame, payload in zip(frames, payloads):
        w, h = frame.size
        entries += struct.pack(
            "<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)

    path.write_bytes(header + entries + b"".join(payloads))


def main():
    frames = [draw(size) for size in SIZES]
    write_ico(frames, HERE / "hail.ico")
    frames[-1].save(HERE / "hail.png")
    print(f"wrote hail.ico ({len(frames)} sizes) and hail.png")


if __name__ == "__main__":
    main()
