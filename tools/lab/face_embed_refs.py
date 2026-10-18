# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = [
#     "insightface==2.1",
#     "onnxruntime==1.31.0",
#     "numpy==2.2.6",
#     "pillow==12.3.0",
# ]
# ///
# SPDX-License-Identifier: Apache-2.0
"""Parity references for the face fingerprints (spec §7, §14): ArcFace w600k_r50 (buffalo_l) and SFace (OpenCV Zoo).

Runs in the same separate environment as face_refs.py (`uv run --script face_embed_refs.py`), on the faces found by
face_refs.py (run it first): the 5 keypoints of each face come from its index.json.

ArcFace: insightface's own pipeline, `face_align.norm_crop` (similarity transform of the 5 points onto the ArcFace
112×112 template, cv2.warpAffine bilinear, black border) then `ArcFaceONNX.get_feat` ((x − 127.5) / 127.5, RGB).
SFace: OpenCV `FaceRecognizerSF.alignCrop` + `feature` (RGB 0..255).
warpAffine: random images and affine matrices (rotation, scale, shifts out of the image) for the bit-exact port.

Per photo (stem) and face k:
  <stem>.f<k>.arc_crop.rgb   aligned crop, RGB uint8 112×112
  <stem>.f<k>.arc_input.bin  float32 [1,3,112,112]
  <stem>.f<k>.arc_emb.bin    float32 [512], raw output
  <stem>.f<k>.sface_crop.rgb aligned crop of FaceRecognizerSF, RGB uint8 112×112
  <stem>.f<k>.sface_emb.bin  float32 [128], raw output
  warp_<i>.src.rgb / warp_<i>.dst.rgb  generic warpAffine cases
index.json: per face the matrices (ArcFace M), versions; per warp case the source size, matrix and output size.

Usage:
    uv run --script face_embed_refs.py [--limit-faces 200]
Output: PHOTOFLOW_TESTDATA/parity/faces/ (next to the face_refs.py references)
"""

from __future__ import annotations

import argparse
import json

import cv2
import insightface
import numpy as np
import onnxruntime as ort
from insightface.model_zoo import model_zoo
from insightface.utils import face_align

import labcommon as lc


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--limit-faces", type=int, default=200)
    args = parser.parse_args()
    folder = lc.output_dir("parity", "faces")
    index = json.loads((folder / "index.json").read_text(encoding="utf-8"))
    arcface = model_zoo.get_model(str(lc.model_file("insightface-buffalo-l", "w600k_r50.onnx")), providers=["CPUExecutionProvider"])
    sface = cv2.FaceRecognizerSF.create(str(lc.model_file("opencv-sface-2021dec", "face_recognition_sface_2021dec.onnx")), "")

    faces_out = []
    count = 0
    for photo in index["photos"]:
        stem = photo["image"]
        rgb = np.fromfile(folder / f"{stem}.rgb", dtype=np.uint8).reshape(photo["height"], photo["width"], 3)
        bgr = np.ascontiguousarray(rgb[:, :, ::-1])
        for k, face in enumerate(photo["faces"]):
            if count >= args.limit_faces:
                break
            kps = np.array(face["kps"], dtype=np.float32).reshape(5, 2)
            matrix = face_align.estimate_norm(kps, 112)
            crop = face_align.norm_crop(bgr, kps, 112)
            blob = cv2.dnn.blobFromImages([crop], 1.0 / arcface.input_std, arcface.input_size,
                                          (arcface.input_mean,) * 3, swapRB=True)
            embedding = arcface.session.run(arcface.output_names, {arcface.input_name: blob})[0][0]
            (folder / f"{stem}.f{k}.arc_crop.rgb").write_bytes(np.ascontiguousarray(crop[:, :, ::-1]).tobytes())
            blob.astype(np.float32).tofile(folder / f"{stem}.f{k}.arc_input.bin")
            embedding.astype(np.float32).tofile(folder / f"{stem}.f{k}.arc_emb.bin")

            box = face["box"]
            row = np.array([[box[0], box[1], box[2] - box[0], box[3] - box[1], *face["kps"], face["score"]]], dtype=np.float32)
            aligned = sface.alignCrop(bgr, row)
            feature = sface.feature(aligned)[0]
            (folder / f"{stem}.f{k}.sface_crop.rgb").write_bytes(np.ascontiguousarray(aligned[:, :, ::-1]).tobytes())
            feature.astype(np.float32).tofile(folder / f"{stem}.f{k}.sface_emb.bin")
            faces_out.append({"image": stem, "face": k, "arc_matrix": matrix.tolist()})
            count += 1

    rng = np.random.default_rng(12)
    warps = []
    for i in range(24):
        height, width = int(rng.integers(40, 400)), int(rng.integers(40, 400))
        src = rng.integers(0, 256, size=(height, width, 3), dtype=np.uint8)
        angle, scale = float(rng.uniform(-40, 40)), float(rng.uniform(0.2, 3.0))
        center = (float(rng.uniform(-20, width + 20)), float(rng.uniform(-20, height + 20)))
        matrix = cv2.getRotationMatrix2D(center, angle, scale)
        matrix[:, 2] += rng.uniform(-30, 30, size=2)
        dst = cv2.warpAffine(src, matrix, (112, 112), borderValue=0.0)
        (folder / f"warp_{i}.src.rgb").write_bytes(src.tobytes())
        (folder / f"warp_{i}.dst.rgb").write_bytes(dst.tobytes())
        warps.append({"width": width, "height": height, "matrix": matrix.tolist()})

    out = {
        "insightface": insightface.__version__,
        "opencv": cv2.__version__,
        "onnxruntime": ort.__version__,
        "faces": faces_out,
        "warps": warps,
    }
    (folder / "embed_index.json").write_text(json.dumps(out, indent=1), encoding="utf-8")
    print(f"{count} faces, {len(warps)} warp cases, OpenCV {cv2.__version__}")


if __name__ == "__main__":
    main()
