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
"""Parity references for the buffalo_l face adapter (SCRFD det_10g + 2d106det), spec §7, §14.

Runs in its own environment (insightface 2.1 needs opencv-python, which conflicts with the lab's
opencv-python-headless 5.0): `uv run --script face_refs.py ...`.

The C# adapter prepares the model inputs with Pillow-compatible antialiased resampling instead of
cv2.resize / cv2.warpAffine (which point-sample when downscaling large previews). The references therefore use
the same preparation in Python, and insightface's own code for the model run and the post-processing
(anchors, distance decoding, NMS, landmark de-normalization). A second pass runs the unmodified insightface
pipeline (cv2) and reports how much the two preparations differ (lab information, not a test).

Per photo (stem):
  <stem>.rgb                 analysis image, RGB uint8, long side <= 2048, orientation applied
  <stem>.det_input.bin       float32 [1,3,640,640]
  <stem>.det_out<i>.bin      the 9 SCRFD outputs (scores, distances, keypoints for strides 8/16/32)
  <stem>.f<k>.lmk_input.bin  float32 [1,3,192,192] for face k
  <stem>.f<k>.lmk_out.bin    float32 [1,212]
index.json: sizes, detections (pixel boxes, scores, 5 keypoints, 106 landmarks), versions.

Usage:
    uv run --script face_refs.py --photos E:/path/outside/repo/photos [--limit 40]
Output: PHOTOFLOW_TESTDATA/parity/faces/
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import cv2
import insightface
import numpy as np
import onnxruntime as ort
from insightface.model_zoo import model_zoo
from PIL import Image

import labcommon as lc

ANALYSIS_LONG_SIDE = 2048
DET_SIZE = 640
LMK_SIZE = 192
DET_THRESHOLD = 0.5
NMS_THRESHOLD = 0.4
# 2d106det eye contours (corner, lower lid x3, corner, upper lid x3), determined on the lab photos.
LEFT_EYE = [35, 36, 33, 37, 39, 42, 40, 41]
RIGHT_EYE = [89, 90, 87, 91, 93, 96, 94, 95]


def analysis_image(image: Image.Image) -> Image.Image:
    scale = ANALYSIS_LONG_SIDE / max(image.size)
    if scale >= 1:
        return image
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    return image.resize(size, Image.Resampling.BILINEAR)


def letterbox(image: Image.Image) -> tuple[np.ndarray, float]:
    """Size computation of insightface SCRFD.detect, Pillow bilinear instead of cv2.resize."""
    im_ratio = image.height / image.width
    if im_ratio > 1.0:
        new_height = DET_SIZE
        new_width = int(new_height / im_ratio)
    else:
        new_width = DET_SIZE
        new_height = int(new_width * im_ratio)
    det_scale = new_height / image.height
    canvas = np.zeros((DET_SIZE, DET_SIZE, 3), dtype=np.uint8)
    canvas[:new_height, :new_width] = np.asarray(image.resize((new_width, new_height), Image.Resampling.BILINEAR))
    return canvas, det_scale


def det_blob(canvas_rgb: np.ndarray) -> np.ndarray:
    return ((canvas_rgb.astype(np.float32) - 127.5) / 128.0).transpose(2, 0, 1)[None].copy()


def detect(det, canvas_rgb: np.ndarray, det_scale: float, outputs_sink: list) -> tuple[np.ndarray, np.ndarray]:
    """insightface SCRFD.detect after the resize: forward + scale + sort + NMS, with insightface's code."""
    blob = det_blob(canvas_rgb)
    net_outs = det.session.run(det.output_names, {det.input_name: blob})
    outputs_sink.extend(net_outs)
    # Same decoding as SCRFD.forward (strides, anchors, distance2bbox/kps).
    scores_list, bboxes_list, kpss_list = [], [], []
    for idx, stride in enumerate(det._feat_stride_fpn):
        scores = net_outs[idx]
        bbox_preds = net_outs[idx + det.fmc] * stride
        kps_preds = net_outs[idx + det.fmc * 2] * stride
        height = width = DET_SIZE // stride
        anchor_centers = np.stack(np.mgrid[:height, :width][::-1], axis=-1).astype(np.float32)
        anchor_centers = (anchor_centers * stride).reshape((-1, 2))
        anchor_centers = np.stack([anchor_centers] * det._num_anchors, axis=1).reshape((-1, 2))
        pos_inds = np.where(scores >= DET_THRESHOLD)[0]
        bboxes = insightface.model_zoo.scrfd.distance2bbox(anchor_centers, bbox_preds)
        kpss = insightface.model_zoo.scrfd.distance2kps(anchor_centers, kps_preds).reshape((-1, 5, 2))
        scores_list.append(scores[pos_inds])
        bboxes_list.append(bboxes[pos_inds])
        kpss_list.append(kpss[pos_inds])
    scores = np.vstack(scores_list)
    order = scores.ravel().argsort()[::-1]
    bboxes = np.vstack(bboxes_list) / det_scale
    kpss = np.vstack(kpss_list) / det_scale
    pre_det = np.hstack((bboxes, scores)).astype(np.float32, copy=False)[order, :]
    det.nms_thresh = NMS_THRESHOLD
    keep = det.nms(pre_det)
    return pre_det[keep, :], kpss[order, :, :][keep, :, :]


def crop_box(bbox: np.ndarray) -> tuple[float, float, float, float, float]:
    """Source box equivalent to insightface's warpAffine (center, scale 192 / (1.5 * max side)), pixel-center aligned."""
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    cx, cy = (bbox[2] + bbox[0]) / 2, (bbox[3] + bbox[1]) / 2
    s = LMK_SIZE / (max(w, h) * 1.5)
    x0 = cx + 0.5 - (LMK_SIZE / 2 + 0.5) / s
    y0 = cy + 0.5 - (LMK_SIZE / 2 + 0.5) / s
    return float(x0), float(y0), float(x0 + LMK_SIZE / s), float(y0 + LMK_SIZE / s), float(s)


def landmark_input(rgb: np.ndarray, box: tuple[float, float, float, float]) -> np.ndarray:
    """Zero-padded integer crop around the box, then Pillow resize with the fractional box (float32 like Pillow)."""
    x0, y0, x1, y1 = box
    ix0, iy0, ix1, iy1 = math.floor(x0), math.floor(y0), math.ceil(x1), math.ceil(y1)
    h, w = rgb.shape[:2]
    crop = np.zeros((iy1 - iy0, ix1 - ix0, 3), dtype=np.uint8)
    sx0, sy0, sx1, sy1 = max(ix0, 0), max(iy0, 0), min(ix1, w), min(iy1, h)
    if sx1 > sx0 and sy1 > sy0:
        crop[sy0 - iy0:sy1 - iy0, sx0 - ix0:sx1 - ix0] = rgb[sy0:sy1, sx0:sx1]
    rel = tuple(float(np.float32(v)) for v in (x0 - ix0, y0 - iy0, x1 - ix0, y1 - iy0))
    resized = Image.fromarray(crop).resize((LMK_SIZE, LMK_SIZE), Image.Resampling.BILINEAR, box=rel)
    return np.asarray(resized, dtype=np.float32).transpose(2, 0, 1)[None].copy()


def landmarks(lmk, blob: np.ndarray, bbox: np.ndarray, sink: list) -> np.ndarray:
    """insightface Landmark.get after the crop: run, de-normalize, inverse affine transform."""
    raw = lmk.session.run(lmk.output_names, {lmk.input_name: blob})[0]
    sink.append(raw)
    pred = raw[0].reshape((-1, 2)).copy()
    pred[:, 0:2] += 1
    pred[:, 0:2] *= LMK_SIZE // 2
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    cx, cy = (bbox[2] + bbox[0]) / 2, (bbox[3] + bbox[1]) / 2
    s = LMK_SIZE / (max(w, h) * 1.5)
    m = np.array([[s, 0, LMK_SIZE / 2 - cx * s], [0, s, LMK_SIZE / 2 - cy * s]], dtype=np.float64)
    return insightface.utils.face_align.trans_points(pred, cv2.invertAffineTransform(m))


def eye_ratio(points: np.ndarray, idx: list[int]) -> float:
    p = points[idx]
    vertical = np.linalg.norm(p[1] - p[7]) + np.linalg.norm(p[2] - p[6]) + np.linalg.norm(p[3] - p[5])
    return float(vertical / (3 * np.linalg.norm(p[0] - p[4])))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=40)
    args = parser.parse_args()
    photos_dir = args.photos.resolve()
    lc.ensure_outside_repo(photos_dir)
    folder = lc.model_dir("insightface-buffalo-l")
    providers = ["CPUExecutionProvider"]
    det = model_zoo.get_model(str(folder / "det_10g.onnx"), providers=providers)
    det.prepare(ctx_id=-1, input_size=(DET_SIZE, DET_SIZE), det_thresh=DET_THRESHOLD)
    lmk = model_zoo.get_model(str(folder / "2d106det.onnx"), providers=providers)
    lmk.prepare(ctx_id=-1)
    assert (lmk.input_mean, lmk.input_std) == (0.0, 1.0), (lmk.input_mean, lmk.input_std)
    out = lc.output_dir("parity", "faces")

    rows, comparison = [], []
    for path in lc.list_images(photos_dir, include_raw=True):
        if len(rows) >= args.limit:
            break
        image = analysis_image(lc.load_photo(path))
        rgb = np.ascontiguousarray(np.asarray(image))
        canvas, det_scale = letterbox(image)
        det_outputs: list = []
        bboxes, kpss = detect(det, canvas, det_scale, det_outputs)
        if len(bboxes) == 0:
            continue
        stem = path.stem
        rgb.tofile(out / f"{stem}.rgb")
        det_blob(canvas).tofile(out / f"{stem}.det_input.bin")
        for i, o in enumerate(det_outputs):
            o.astype(np.float32).tofile(out / f"{stem}.det_out{i}.bin")
        faces = []
        for k, (bbox, kps) in enumerate(zip(bboxes, kpss)):
            box = crop_box(bbox)
            blob = landmark_input(rgb, box[:4])
            raw: list = []
            points = landmarks(lmk, blob, bbox, raw)
            blob.tofile(out / f"{stem}.f{k}.lmk_input.bin")
            raw[0].astype(np.float32).tofile(out / f"{stem}.f{k}.lmk_out.bin")
            faces.append({"box": [float(v) for v in bbox[:4]], "score": float(bbox[4]), "kps": kps.astype(float).ravel().tolist(),
                          "landmarks": points.astype(float).ravel().tolist(),
                          "eye_ratio": [eye_ratio(points, LEFT_EYE), eye_ratio(points, RIGHT_EYE)]})
        rows.append({"image": stem, "width": image.width, "height": image.height, "det_scale": det_scale,
                     "det_outputs": [list(o.shape) for o in det_outputs], "faces": faces})

        # Unmodified insightface pipeline (cv2 resize / warpAffine), BGR input as in insightface.
        bgr = np.ascontiguousarray(rgb[:, :, ::-1])
        ref_boxes, _ = det.detect(bgr, max_num=0, metric="default")
        for face in faces:
            box = np.array(face["box"])
            ious = [iou(box, rb[:4]) for rb in ref_boxes]
            if not ious or max(ious) < 0.5:
                comparison.append({"image": stem, "matched": False})
                continue
            j = int(np.argmax(ious))
            ref_face = insightface.app.common.Face(bbox=ref_boxes[j, :4], det_score=ref_boxes[j, 4])
            ref_points = lmk.get(bgr, ref_face)
            width = box[2] - box[0]
            comparison.append({
                "image": stem, "matched": True, "face_px": float(width), "iou": float(ious[j]),
                "dscore": float(abs(face["score"] - ref_boxes[j, 4])),
                "landmark_mean_px": float(np.linalg.norm(np.array(face["landmarks"]).reshape(-1, 2) - ref_points, axis=1).mean()),
                "eye_ratio": face["eye_ratio"], "eye_ratio_cv2": [eye_ratio(ref_points, LEFT_EYE), eye_ratio(ref_points, RIGHT_EYE)],
            })
        print(f"{stem}: {len(faces)} faces")

    index = {"insightface": insightface.__version__, "onnxruntime": ort.__version__, "det_threshold": DET_THRESHOLD,
             "nms_threshold": NMS_THRESHOLD, "photos": rows}
    (out / "index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")
    (out / "cv2_comparison.json").write_text(json.dumps(comparison, indent=1), encoding="utf-8")
    matched = [c for c in comparison if c["matched"]]
    if matched:
        print(f"{len(rows)} photos, {len(comparison)} faces, {len(matched)} matched with the cv2 pipeline")
        print("  IoU min", round(min(c["iou"] for c in matched), 4), " |dscore| max", round(max(c["dscore"] for c in matched), 4))
        big = [c for c in matched if c["face_px"] >= 80]
        for name, group in (("all", matched), (">=80px", big)):
            if group:
                lm = sorted(c["landmark_mean_px"] / c["face_px"] for c in group)
                er = sorted(abs(a - b) for c in group for a, b in zip(c["eye_ratio"], c["eye_ratio_cv2"]))
                print(f"  {name}: n={len(group)} landmark error/face median {lm[len(lm) // 2]:.4f} max {lm[-1]:.4f};"
                      f" |eye ratio diff| median {er[len(er) // 2]:.4f} max {er[-1]:.4f}")
    print(f"References in {out}")


def iou(a: np.ndarray, b: np.ndarray) -> float:
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


if __name__ == "__main__":
    main()
