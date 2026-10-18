# SPDX-License-Identifier: Apache-2.0
"""Parity references for the OCR adapters in C# (spec §8, decision F4-1).

Models (RapidOCR conversions of PaddleOCR, SWHL/RapidOCR@1cfba2e): ch_PP-OCRv4_det (DB text detection) and
en_PP-OCRv3_rec (CTC recognition, dictionary in the model metadata). As for the face models, the inputs are prepared
with antialiased Pillow resampling instead of cv2.resize; the C# adapters do the same. Box extraction is the simple DB
post-processing implemented identically here and in C#: probability > 0.3, 8-connected components, score = mean
probability of the component, kept when >= 0.5 and both sides >= 3, then expanded by area * 1.6 / perimeter.
RapidOCR's own pipeline (cv2, rotated boxes) runs too, for an informative comparison of the texts.

Per image (stem): <stem>.rgb (input, RGB bytes), <stem>.det_input.bin (float32 [1,3,H,W]), <stem>.det_prob.bin
(float32 [H,W]); index.json: sizes, boxes (in input pixels) with score, and for each box the recognized text and
confidence; RapidOCR texts.

Usage:
    uv run python ocr_refs.py --photos E:/path/outside/repo/photos [--limit 25]
Output: PHOTOFLOW_TESTDATA/parity/ocr/
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image
from scipy import ndimage

import labcommon as lc

DET_MIN_SIDE = 736
DET_THRESH = 0.3
BOX_THRESH = 0.5
UNCLIP = 1.6
REC_HEIGHT = 48
REC_MAX_WIDTH = 320
INPUT_LONG_SIDE = 1024


def det_input(image: Image.Image) -> tuple[np.ndarray, int, int]:
    """RapidOCR DetPreProcess (limit_type 'min', 736, mean/std 0.5) with Pillow bilinear, sizes rounded to 32."""
    w, h = image.size
    ratio = DET_MIN_SIDE / min(w, h) if min(w, h) < DET_MIN_SIDE else 1.0
    rw = max(32, int(round(int(w * ratio) / 32) * 32))
    rh = max(32, int(round(int(h * ratio) / 32) * 32))
    resized = image.resize((rw, rh), Image.Resampling.BILINEAR)
    array = np.asarray(resized, dtype=np.float32) * np.float32(1 / 255.0)
    array = (array - np.float32(0.5)) / np.float32(0.5)
    return array.transpose(2, 0, 1)[None].astype(np.float32), rw, rh


def boxes_from(prob: np.ndarray) -> list[dict]:
    labels, count = ndimage.label(prob > DET_THRESH, structure=np.ones((3, 3), dtype=int))
    height, width = prob.shape
    boxes = []
    for index, slices in enumerate(ndimage.find_objects(labels), start=1):
        if slices is None:
            continue
        ys, xs = slices
        x0, x1, y0, y1 = xs.start, xs.stop, ys.start, ys.stop
        score = float(prob[labels == index].astype(np.float64).mean())
        bw, bh = x1 - x0, y1 - y0
        if score < BOX_THRESH or min(bw, bh) < 3:
            continue
        distance = bw * bh * UNCLIP / (2 * (bw + bh))
        boxes.append({
            "x0": max(0, int(math.floor(x0 - distance))), "y0": max(0, int(math.floor(y0 - distance))),
            "x1": min(width, int(math.ceil(x1 + distance))), "y1": min(height, int(math.ceil(y1 + distance))),
            "score": score,
        })
    boxes.sort(key=lambda b: (b["y0"], b["x0"]))
    return boxes


def rec_input(crop: Image.Image) -> np.ndarray:
    w, h = crop.size
    width = min(REC_MAX_WIDTH, max(1, int(math.ceil(REC_HEIGHT * w / h))))
    resized = crop.resize((width, REC_HEIGHT), Image.Resampling.BILINEAR)
    array = (np.asarray(resized, dtype=np.float32) / np.float32(255) - np.float32(0.5)) / np.float32(0.5)
    tensor = np.zeros((1, 3, REC_HEIGHT, max(width, REC_HEIGHT)), dtype=np.float32)
    tensor[0, :, :, :width] = array.transpose(2, 0, 1)
    return tensor


def ctc(probabilities: np.ndarray, characters: list[str]) -> tuple[str, float]:
    best = probabilities.argmax(axis=1)
    scores = probabilities.max(axis=1)
    text, confidences, previous = [], [], -1
    for index, score in zip(best, scores):
        if index != previous and index != 0:
            text.append(characters[index - 1])
            confidences.append(float(score))
        previous = index
    return "".join(text), float(np.mean(confidences)) if confidences else 0.0


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=25)
    args = parser.parse_args()
    photos = args.photos.resolve()
    lc.ensure_outside_repo(photos)
    out = lc.output_dir("parity", "ocr")
    folder = lc.model_dir("ppocr")
    det = ort.InferenceSession(str(folder / "ch_PP-OCRv4_det_infer.onnx"), providers=["CPUExecutionProvider"])
    rec = ort.InferenceSession(str(folder / "en_PP-OCRv3_rec_infer.onnx"), providers=["CPUExecutionProvider"])
    characters = rec.get_modelmeta().custom_metadata_map["character"].split("\n") + [" "]

    from rapidocr_onnxruntime import RapidOCR
    rapid = RapidOCR(det_model_path=str(folder / "ch_PP-OCRv4_det_infer.onnx"), rec_model_path=str(folder / "en_PP-OCRv3_rec_infer.onnx"))

    index = {"characters": len(characters), "images": []}
    for path in lc.list_images(photos)[: args.limit]:
        image = lc.load_photo(path)
        scale = INPUT_LONG_SIDE / max(image.size)
        if scale < 1:
            image = image.resize((round(image.width * scale), round(image.height * scale)), Image.Resampling.BILINEAR)
        (out / f"{path.stem}.rgb").write_bytes(np.asarray(image, dtype=np.uint8).tobytes())
        tensor, rw, rh = det_input(image)
        tensor.tofile(out / f"{path.stem}.det_input.bin")
        prob = det.run(None, {"x": tensor})[0][0, 0].astype(np.float32)
        prob.tofile(out / f"{path.stem}.det_prob.bin")
        boxes = boxes_from(prob)
        resized = image.resize((rw, rh), Image.Resampling.BILINEAR)
        for box in boxes:
            crop = resized.crop((box["x0"], box["y0"], box["x1"], box["y1"]))
            text, confidence = ctc(rec.run(None, {"x": rec_input(crop)})[0][0], characters)
            box["text"], box["confidence"] = text, confidence
        rapid_result, _ = rapid(np.asarray(image)[:, :, ::-1].copy())
        index["images"].append({
            "stem": path.stem, "width": image.width, "height": image.height, "det_width": rw, "det_height": rh,
            "boxes": boxes, "rapidocr": [r[1] for r in (rapid_result or [])],
        })
        print(f"{path.name}: {[b['text'] for b in boxes if b['confidence'] > 0.5]} | rapidocr {[r[1] for r in (rapid_result or [])]}")
    (out / "index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
