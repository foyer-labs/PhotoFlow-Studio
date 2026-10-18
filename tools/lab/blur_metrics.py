# SPDX-License-Identifier: Apache-2.0
"""Which sharpness measure separates blurred from sharp photos regardless of content (spec §6)?

Sharp photos of the set are blurred synthetically (Gaussian defocus, linear motion at a seeded random angle) at the
reference scale (long side 2048). For each measure the threshold is set on the originals (the worst 2% and the
worst original) and the share of blurred copies beyond it is reported: a usable absolute discard threshold needs
high detection with few originals beyond it, whatever the content (walls, night scenes, panning).

Measures (gray = OpenCV RGB2GRAY, as the C# ImageSignals):
  lap_full    variance of the Laplacian after a 3x3 Gaussian, whole image (spec §6 measure)
  lap_tile    the same, sharpest tile of a 4x4 grid
  lap_norm    lap_full / variance of the gray image (contrast normalized)
  blur_full   blur effect (Crete-Roffet et al. 2007, skimage.measure.blur_effect, h_size 11): 0 sharp .. 1 blurred
  blur_tile   blur effect of the sharpest tile of the 4x4 grid
  blur_ms     worst blur effect at 2048, 1024 and 512 (Pillow bilinear): a blur much wider than the 11 px window
              saturates the single-scale measure, the reduced scales bring it back into range

Usage:
    uv run python blur_metrics.py --photos E:/path/outside/repo/photos [--limit 230]
Output: PHOTOFLOW_TESTDATA/lab/blur-metrics/report.json
"""

from __future__ import annotations

import argparse
import json

import cv2
import numpy as np
from skimage.measure import blur_effect

import labcommon as lc

REFERENCE = 2048
BLURS = [("gauss1.5", 1.5), ("gauss2.5", 2.5), ("gauss4", 4.0), ("gauss8", 8.0), ("gauss16", 16.0),
         ("motion9", 9), ("motion15", 15), ("motion25", 25), ("motion50", 50), ("motion100", 100)]
LOWER_IS_BLURRED = {"lap_full": True, "lap_tile": True, "lap_norm": True, "blur_full": False, "blur_tile": False, "blur_ms": False}
SCALES = (1024, 512)


def reference_gray(path) -> np.ndarray:
    image = lc.load_photo(path)
    scale = REFERENCE / max(image.size)
    if scale < 1:
        image = image.resize((round(image.width * scale), round(image.height * scale)), resample=2)  # BILINEAR
    return cv2.cvtColor(np.asarray(image), cv2.COLOR_RGB2GRAY)


def blurred(gray: np.ndarray, kind: str, amount: float, rng: np.random.Generator) -> np.ndarray:
    if kind.startswith("gauss"):
        return cv2.GaussianBlur(gray, (0, 0), amount)
    length = int(amount) | 1
    kernel = np.zeros((length, length), np.float32)
    kernel[length // 2, :] = 1.0 / length
    angle = rng.uniform(0, 180)
    rotation = cv2.getRotationMatrix2D((length / 2 - 0.5, length / 2 - 0.5), angle, 1.0)
    kernel = cv2.warpAffine(kernel, rotation, (length, length))
    kernel /= kernel.sum()
    return cv2.filter2D(gray, -1, kernel, borderType=cv2.BORDER_REFLECT_101)


def tiles(gray: np.ndarray):
    h, w = gray.shape
    for ty in range(4):
        for tx in range(4):
            yield gray[h * ty // 4:h * (ty + 1) // 4, w * tx // 4:w * (tx + 1) // 4]


def multiscale_blur(gray: np.ndarray) -> float:
    from PIL import Image

    values = [blur_effect(gray, h_size=11)]
    image = Image.fromarray(gray)
    for side in SCALES:
        scale = side / max(gray.shape)
        size = (max(1, round(gray.shape[1] * scale)), max(1, round(gray.shape[0] * scale)))
        values.append(blur_effect(np.asarray(image.resize(size, resample=2)), h_size=11))
    return float(max(values))


def measures(gray: np.ndarray, fast: bool = False) -> dict:
    if fast:
        return {"blur_full": float(blur_effect(gray, h_size=11)), "blur_ms": multiscale_blur(gray)}
    lap = cv2.Laplacian(cv2.GaussianBlur(gray, (3, 3), 0), cv2.CV_64F)
    h, w = gray.shape
    lap_tiles = [lap[h * ty // 4:h * (ty + 1) // 4, w * tx // 4:w * (tx + 1) // 4].var() for ty in range(4) for tx in range(4)]
    return {
        "lap_full": float(lap.var()),
        "lap_tile": float(max(lap_tiles)),
        "lap_norm": float(lap.var() / max(gray.astype(np.float64).var(), 1e-6)),
        "blur_full": float(blur_effect(gray, h_size=11)),
        "blur_tile": float(min(blur_effect(t, h_size=11) for t in tiles(gray))),
        "blur_ms": multiscale_blur(gray),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", required=True)
    parser.add_argument("--limit", type=int, default=230)
    parser.add_argument("--fast", action="store_true", help="only the blur effect measures")
    args = parser.parse_args()
    from pathlib import Path

    folder = Path(args.photos).resolve()
    lc.ensure_outside_repo(folder)
    rng = np.random.default_rng(12)
    rows = []
    for path in lc.list_images(folder, include_raw=True)[: args.limit]:
        gray = reference_gray(path)
        row = {"image": path.name, "original": measures(gray, args.fast)}
        for name, amount in BLURS:
            row[name] = measures(blurred(gray, name, amount, rng), args.fast)
        rows.append(row)
        print(path.name, {k: round(v, 3) for k, v in row["original"].items()})

    summary = {}
    for metric, lower in LOWER_IS_BLURRED.items():
        if metric not in rows[0]["original"]:
            continue
        sign = 1 if lower else -1
        originals = np.array([r["original"][metric] for r in rows]) * sign
        result = {}
        for label, threshold in (("p2", np.percentile(originals, 2)), ("min", originals.min())):
            result[label] = {name: round(float(np.mean(np.array([r[name][metric] for r in rows]) * sign < threshold)), 3) for name, _ in BLURS}
        summary[metric] = result
        print(metric, json.dumps(result))
    out = lc.output_dir("lab", "blur-metrics")
    (out / "report.json").write_text(json.dumps({"photos": len(rows), "summary": summary, "rows": rows}, indent=1), encoding="utf-8")
    print(f"Report in {out}")


if __name__ == "__main__":
    main()
