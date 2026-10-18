# SPDX-License-Identifier: Apache-2.0
"""Parity references for the RAM++ tagger in C# (spec §8, §14; decision F3-1).

The official RAM++ transform (ram.get_transform): PIL bilinear resize to 384x384 (aspect ratio not kept), ToTensor,
ImageNet normalization. verify_ramplus.py showed that the fp32 ONNX export gives the same tag sets as the PyTorch
original, so the references here come from the ONNX fp32 model on that tensor (CPU): the C# test checks its own
preprocessing against the saved tensor and its probabilities and tag set against the saved ones.

Per photo (stem): <stem>.rgb (analysis image, RGB bytes, long side <= 2048; the C# input), <stem>.ram_input.bin
(float32 [1,3,384,384]), <stem>.ram_ref.bin (float32 [4585] probabilities). index.json: sizes and tag sets at the
official thresholds.

Usage:
    uv run python ramplus_refs.py --photos E:/path/outside/repo/photos [--limit 30]
Output: PHOTOFLOW_TESTDATA/parity/ramplus/
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image

import labcommon as lc

MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


def analysis_image(image: Image.Image) -> Image.Image:
    scale = 2048 / max(image.size)
    if scale >= 1:
        return image
    return image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))), Image.Resampling.BILINEAR)


def ram_input(image: Image.Image) -> np.ndarray:
    """ram.get_transform(384) without torch: same operations, same float32 arithmetic."""
    resized = image.convert("RGB").resize((384, 384), Image.Resampling.BILINEAR)
    array = np.asarray(resized, dtype=np.float32) / np.float32(255)
    array = (array - MEAN) / STD
    return array.transpose(2, 0, 1)[None].astype(np.float32)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=30)
    args = parser.parse_args()
    photos = args.photos.resolve()
    lc.ensure_outside_repo(photos)
    out = lc.output_dir("parity", "ramplus")

    folder = lc.model_dir("ramplus-fp32")
    session = ort.InferenceSession(str(folder / "ram_plus.onnx"), providers=["CPUExecutionProvider"])
    tags = (folder / "ram_tag_list.txt").read_text(encoding="utf-8").splitlines()
    thresholds = np.array([float(v) for v in (folder / "ram_tag_list_threshold.txt").read_text(encoding="utf-8").split()])

    index = {"model": "ramplus-fp32/ram_plus.onnx", "photos": []}
    for path in lc.list_images(photos, include_raw=True)[: args.limit]:
        image = analysis_image(lc.load_photo(path))
        (out / f"{path.stem}.rgb").write_bytes(np.asarray(image, dtype=np.uint8).tobytes())
        tensor = ram_input(image)
        tensor.tofile(out / f"{path.stem}.ram_input.bin")
        logits = session.run(None, {"input": tensor})[0][0]
        probabilities = (1 / (1 + np.exp(-logits.astype(np.float64)))).astype(np.float32)
        probabilities.tofile(out / f"{path.stem}.ram_ref.bin")
        chosen = sorted(tags[i] for i in np.flatnonzero(probabilities > thresholds))
        index["photos"].append({"stem": path.stem, "width": image.width, "height": image.height, "tags": chosen})
        print(f"{path.name}: {', '.join(chosen)}")
    (out / "index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
