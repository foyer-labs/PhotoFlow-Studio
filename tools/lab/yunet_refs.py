# SPDX-License-Identifier: Apache-2.0
"""Parity references for the YuNet face adapter (spec §7, §14, decision A5): OpenCV 5.0 FaceDetectorYN.

The model has a fixed 640x640 input (note N4): the photo is reduced to fit (Pillow bilinear, as the C# adapter) and
placed top-left on a black 640x640 canvas. OpenCV's FaceDetectorYN runs on that canvas (BGR, no scaling) with its
own decoding and NMS; the raw ONNX Runtime outputs are saved for the tensor-level comparison.

Per photo (stem): <stem>.rgb (analysis image, long side <= 2048), <stem>.yunet_input.bin (float32 [1,3,640,640], BGR),
<stem>.yunet_out<i>.bin (the 12 outputs). index.json: sizes, scale, detections in canvas pixels (x, y, w, h, 5 points, score).

Usage:
    uv run python yunet_refs.py --photos E:/path/outside/repo/photos [--limit 40]
Output: PHOTOFLOW_TESTDATA/parity/yunet/
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort
from PIL import Image

import labcommon as lc

SIZE = 640
SCORE_THRESHOLD = 0.6
NMS_THRESHOLD = 0.3
TOP_K = 5000


def analysis_image(image: Image.Image) -> Image.Image:
    scale = 2048 / max(image.size)
    if scale >= 1:
        return image
    return image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))), Image.Resampling.BILINEAR)


def letterbox(image: Image.Image) -> tuple[np.ndarray, float]:
    scale = SIZE / max(image.size)
    width, height = max(1, int(image.width * scale)), max(1, int(image.height * scale))
    canvas = np.zeros((SIZE, SIZE, 3), dtype=np.uint8)
    canvas[:height, :width] = np.asarray(image.resize((width, height), Image.Resampling.BILINEAR))
    return canvas, scale


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=40)
    args = parser.parse_args()
    photos = args.photos.resolve()
    lc.ensure_outside_repo(photos)
    model = lc.model_file("opencv-yunet-2023mar", "face_detection_yunet_2023mar.onnx")
    detector = cv2.FaceDetectorYN.create(str(model), "", (SIZE, SIZE), SCORE_THRESHOLD, NMS_THRESHOLD, TOP_K)
    session = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
    out = lc.output_dir("parity", "yunet")

    rows = []
    for path in lc.list_images(photos, include_raw=True):
        if len(rows) >= args.limit:
            break
        image = analysis_image(lc.load_photo(path))
        canvas, scale = letterbox(image)
        bgr = np.ascontiguousarray(canvas[:, :, ::-1])
        _, faces = detector.detect(bgr)
        if faces is None or len(faces) == 0:
            continue
        stem = path.stem
        np.ascontiguousarray(np.asarray(image)).tofile(out / f"{stem}.rgb")
        blob = bgr.astype(np.float32).transpose(2, 0, 1)[None].copy()
        blob.tofile(out / f"{stem}.yunet_input.bin")
        for i, o in enumerate(session.run(None, {"input": blob})):
            o.astype(np.float32).tofile(out / f"{stem}.yunet_out{i}.bin")
        rows.append({"image": stem, "width": image.width, "height": image.height, "scale": scale,
                     "faces": [[float(v) for v in f] for f in faces]})
        print(f"{stem}: {len(faces)} faces")

    index = {"opencv": cv2.__version__, "onnxruntime": ort.__version__, "score_threshold": SCORE_THRESHOLD,
             "nms_threshold": NMS_THRESHOLD, "top_k": TOP_K, "photos": rows}
    (out / "index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")
    print(f"{len(rows)} photos with faces, references in {out}")


if __name__ == "__main__":
    main()
