# SPDX-License-Identifier: Apache-2.0
"""Generate D-FINE parity references from the original PyTorch model (spec §14, decision A8).

For each image of the set, with the official preprocessor (RTDetrImageProcessor, Pillow backend):
  - <stem>.input.bin   pixel_values float32 [1,3,640,640] (little endian)
  - <stem>.logits.bin  PyTorch logits float32 [1,300,80]
  - <stem>.boxes.bin   PyTorch pred_boxes float32 [1,300,4]
  - <stem>.json        shapes, image path, official post-processed detections (normalized boxes)
plus index.json (versions, environment) and python_check.json: a first comparison of the ONNX files
(our export and onnx-community) against PyTorch, done in Python before the C# test.

Usage:
    uv run python parity_refs.py --images E:/path/outside/repo/photos-parity
Output: PHOTOFLOW_TESTDATA/parity/dfine-{s,m}/
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
import transformers
from PIL import Image
from transformers import DFineForObjectDetection, RTDetrImageProcessor

import labcommon as lc
from export_dfine import CHECKPOINTS

THRESHOLD = 0.5


def save_bin(path: Path, array: np.ndarray) -> None:
    np.ascontiguousarray(array, dtype="<f4").tofile(path)


def detections_to_json(result: dict, id2label: dict) -> list[dict]:
    return [
        {"class_id": int(label), "label": id2label[int(label)], "score": float(score),
         "box": [float(v) for v in box]}
        for score, label, box in zip(result["scores"], result["labels"], result["boxes"])
    ]


def compare_raw(a_logits, a_boxes, b_logits, b_boxes) -> dict:
    return {"max_abs_diff_logits": float(np.abs(a_logits - b_logits).max()),
            "max_abs_diff_boxes": float(np.abs(a_boxes - b_boxes).max())}


def generate(variant: str, images: list[Path], testdata: Path) -> None:
    repo, revision = CHECKPOINTS[variant]
    out_dir = lc.output_dir("parity", f"dfine-{variant}")
    cache = lc.output_dir("hf-cache")
    processor = RTDetrImageProcessor.from_pretrained(repo, revision=revision, cache_dir=cache)
    model = DFineForObjectDetection.from_pretrained(repo, revision=revision, cache_dir=cache).eval()
    id2label = model.config.id2label

    onnx_files = {
        "export": testdata / "models" / f"dfine-{variant}-obj2coco-export" / "model.onnx",
        "onnx-community": testdata / "models" / f"dfine-{variant}-obj2coco" / "model.onnx",
    }
    sessions = {name: ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
                for name, path in onnx_files.items() if path.exists()}

    items, checks = [], {name: [] for name in sessions}
    for image_path in images:
        stem = image_path.stem
        image = Image.open(image_path).convert("RGB")  # raw pixel order, no EXIF transpose (same as the C# decoder)
        pixel_values = processor(images=image, return_tensors="pt")["pixel_values"]
        with torch.no_grad():
            outputs = model(pixel_values=pixel_values)
        logits = outputs.logits.numpy()
        boxes = outputs.pred_boxes.numpy()
        result = processor.post_process_object_detection(outputs, threshold=THRESHOLD, target_sizes=None)[0]

        save_bin(out_dir / f"{stem}.input.bin", pixel_values.numpy())
        save_bin(out_dir / f"{stem}.logits.bin", logits)
        save_bin(out_dir / f"{stem}.boxes.bin", boxes)
        meta = {
            "image": image_path.relative_to(testdata).as_posix(),
            "width": image.width, "height": image.height,
            "input_shape": list(pixel_values.shape), "logits_shape": list(logits.shape), "boxes_shape": list(boxes.shape),
            "threshold": THRESHOLD,
            "detections": detections_to_json(result, id2label),
        }
        (out_dir / f"{stem}.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
        items.append(stem)

        for name, session in sessions.items():
            o_logits, o_boxes = session.run(["logits", "pred_boxes"], {"pixel_values": pixel_values.numpy()})
            checks[name].append({"image": stem, **compare_raw(o_logits, o_boxes, logits, boxes)})
        print(f"  {stem}: {len(meta['detections'])} detections")

    index = {
        "model": repo, "revision": revision, "variant": variant, "threshold": THRESHOLD,
        "preprocessor": "RTDetrImageProcessor (Pillow): resize 640x640 bilinear, rescale 1/255, no normalization",
        "postprocess": "post_process_object_detection(use_focal_loss=True), target_sizes=None (normalized xyxy)",
        "items": items,
        "onnx_files": {name: {"path": str(path), "sha256": lc.sha256_of(path)} for name, path in onnx_files.items() if path.exists()},
        "versions": {"torch": torch.__version__, "transformers": transformers.__version__, "onnxruntime": ort.__version__},
        "environment": lc.environment_info(),
    }
    (out_dir / "index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
    summary = {name: {"max_abs_diff_logits": max(c["max_abs_diff_logits"] for c in values),
                      "max_abs_diff_boxes": max(c["max_abs_diff_boxes"] for c in values),
                      "per_image": values}
               for name, values in checks.items() if values}
    (out_dir / "python_check.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    for name, values in summary.items():
        print(f"  ONNX {name:>14} vs PyTorch: max |Δ| logits {values['max_abs_diff_logits']:.2e}, boxes {values['max_abs_diff_boxes']:.2e}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--images", type=Path, required=True, help="folder of images, outside the repository")
    parser.add_argument("--variant", choices=sorted(CHECKPOINTS), action="append")
    parser.add_argument("--limit", type=int, default=0, help="use only the first N images")
    parser.add_argument("--sample", type=int, default=0, help="use N images evenly spaced over the sorted set")
    args = parser.parse_args()

    testdata = lc.testdata_root()
    images_dir = args.images.resolve()
    lc.ensure_outside_repo(images_dir)
    if testdata not in images_dir.parents:
        raise SystemExit("The image folder must be inside PHOTOFLOW_TESTDATA (paths are stored relative to it).")
    images = lc.list_images(images_dir)
    if args.limit:
        images = images[: args.limit]
    if args.sample and args.sample < len(images):
        step = len(images) / args.sample
        images = [images[int(i * step)] for i in range(args.sample)]
    if not images:
        raise SystemExit(f"No images found in {images_dir}")

    for variant in args.variant or sorted(CHECKPOINTS, reverse=True):
        print(f"D-FINE {variant.upper()}: {len(images)} images")
        generate(variant, images, testdata)


if __name__ == "__main__":
    main()
