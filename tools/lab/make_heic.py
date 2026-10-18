# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = ["pillow==12.3.0", "pillow-heif==1.1.1"]
# ///
# SPDX-License-Identifier: Apache-2.0
"""Synthetic HEIC test images for tests/data/public (no third-party content: generated here, CC0).

Writes a small 3-channel gradient with a capture date (no EXIF orientation: in HEIF the rotation belongs to the
container, irot), so that the C# handler can be checked for decoding and EXIF reading.

Usage:
    uv run --script make_heic.py <output.heic>
"""

import sys

import pillow_heif
from PIL import Image

pillow_heif.register_heif_opener()

width, height = 96, 64
image = Image.new("RGB", (width, height))
pixels = image.load()
for y in range(height):
    for x in range(width):
        pixels[x, y] = (x * 255 // width, y * 255 // height, 128)

exif = Image.Exif()
exif[0x0132] = "2026:10:12 10:02:03"  # DateTime
exif[0x010F] = "PhotoFlow"  # Make
image.save(sys.argv[1], format="HEIF", quality=90, exif=exif.tobytes())
print(f"written {sys.argv[1]}")
