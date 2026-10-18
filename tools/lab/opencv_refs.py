# SPDX-License-Identifier: Apache-2.0
"""Reference values from OpenCV for the C# image signals (decision F1-4).

Prints, for deterministic synthetic images and for the photos of a folder, the OpenCV results of the operations
reimplemented in C#: RGB->gray, 3x3 Gaussian blur (8 bit), Laplacian (CV_64F) variance on the whole image and on a zone,
and the blur effect of scikit-image (Crete-Roffet 2007, h_size 11) on the gray image and on a zone crop.

Usage:
    uv run python opencv_refs.py synthetic          # values hard-coded in the C# unit tests
    uv run python opencv_refs.py photos <folder>    # JSON for the local parity test (PHOTOFLOW_TESTDATA/parity/opencv)
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image
from skimage.measure import blur_effect

import labcommon as lc


def synthetic(width: int, height: int) -> np.ndarray:
    y, x = np.mgrid[:height, :width]
    r = (x * 37 + y * 11) % 256
    g = (x * 5 + y * 53) % 256
    b = (x * y * 29 + 7) % 256
    return np.stack([r, g, b], axis=-1).astype(np.uint8)


def multiscale_blur(gray: np.ndarray, min_size: int = 32) -> float:
    """Worst blur effect at full, half and quarter size (Pillow bilinear on the gray image), as CullingAnalyzer."""
    values = [blur_effect(gray, h_size=11)]
    for factor in (2, 4):
        w, h = round(gray.shape[1] / factor), round(gray.shape[0] / factor)
        if min(w, h) < min_size:
            break
        values.append(blur_effect(np.asarray(Image.fromarray(gray).resize((w, h), Image.Resampling.BILINEAR)), h_size=11))
    return float(max(values))


def signals(rgb: np.ndarray, zone: tuple[int, int, int, int]) -> dict:
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    blur = cv2.GaussianBlur(gray, (3, 3), 0)
    lap = cv2.Laplacian(blur, cv2.CV_64F)
    x, y, w, h = zone
    return {
        "gray": gray,
        "blur": blur,
        "variance_full": float(lap.var()),
        "variance_zone": float(lap[y:y + h, x:x + w].var()),
        "blur_full": float(blur_effect(gray, h_size=11)),
        "blur_zone": float(blur_effect(gray[y:y + h, x:x + w], h_size=11)),
        "blur_multiscale_full": multiscale_blur(gray),
        "blur_multiscale_zone": multiscale_blur(np.ascontiguousarray(gray[y:y + h, x:x + w])),
    }


def main() -> None:
    mode = sys.argv[1] if len(sys.argv) > 1 else "synthetic"
    if mode == "synthetic":
        rgb = synthetic(13, 9)
        s = signals(rgb, (2, 1, 7, 5))
        print("GRAY", ",".join(map(str, s["gray"].flatten())))
        print("BLUR", ",".join(map(str, s["blur"].flatten())))
        print(f"VAR_FULL {s['variance_full']!r}")
        print(f"VAR_ZONE {s['variance_zone']!r}")
        print(f"BLUR_FULL {s['blur_full']!r}")
        print(f"BLUR_ZONE {s['blur_zone']!r}")
        big = signals(synthetic(64, 48), (5, 7, 30, 20))
        print(f"BLUR_64x48 {big['blur_full']!r} ZONE {big['blur_zone']!r}")
        return

    folder = Path(sys.argv[2]).resolve()
    out = lc.output_dir("parity", "opencv")
    rows = []
    for path in lc.list_images(folder)[:40]:
        image = Image.open(path).convert("RGB")
        image.thumbnail((1280, 1280), Image.Resampling.BILINEAR)
        rgb = np.asarray(image)
        h, w = rgb.shape[:2]
        zone = (w // 4, h // 4, w // 2, h // 2)
        s = signals(rgb, zone)
        np.ascontiguousarray(rgb).tofile(out / f"{path.stem}.rgb")
        rows.append({"image": path.stem, "width": w, "height": h, "zone": list(zone),
                     "variance_full": s["variance_full"], "variance_zone": s["variance_zone"],
                     "blur_full": s["blur_full"], "blur_zone": s["blur_zone"],
                     "blur_multiscale_full": s["blur_multiscale_full"], "blur_multiscale_zone": s["blur_multiscale_zone"]})
    (out / "index.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    print(f"{len(rows)} references in {out}")


if __name__ == "__main__":
    main()
