# SPDX-License-Identifier: Apache-2.0
"""Per-stage timing benchmark on a folder of photos (spec §5 "Stime di tempo", §13 phase 0).

Stages, each measured with ONNX Runtime on CUDA and on CPU (the runtime used by the C# app):
  preview   embedded JPEG from RAW via exiftool -stay_open (fallback PreviewImage), or JPEG decode;
            decode + downscale to the analysis preview (long side 1280)            [CPU only]
  dfine-s   D-FINE S obj2coco, 640x640
  dfine-m   D-FINE M obj2coco, 640x640
  faces     buffalo_l: det_10g (640) + per face 2d106det (192) + w600k_r50 (112)
  clip      CLIP ViT-B/32 image encoder (quantized), 224
  ramplus   RAM++ int8, 384

Model timings are what matters; face pre-processing is simplified (crops instead of alignment), which
does not change inference cost. Warm-up runs are excluded.

Usage:
    uv run python benchmark.py --photos E:/path/outside/repo/photos-benchmark [--limit 200] [--providers cuda cpu]
Output: PHOTOFLOW_TESTDATA/lab/benchmark/<timestamp>/report.md + report.json
"""

from __future__ import annotations

import argparse
import io
import json
import statistics
import subprocess
import time
from collections import Counter
from datetime import datetime
from pathlib import Path

try:
    import torch  # noqa: F401 - loads the CUDA/cuDNN DLLs used by onnxruntime-gpu
except ImportError:  # DirectML-only environment (onnxruntime-directml)
    pass
import numpy as np
import onnxruntime as ort
from PIL import Image

import labcommon as lc

ort.set_default_logger_severity(3)  # errors only: CUDA EP warnings about memcpy/scatter are expected

PREVIEW_LONG_SIDE = 1280
WARMUP = 3
PROVIDERS = {
    "cuda": ["CUDAExecutionProvider", "CPUExecutionProvider"],
    "dml": ["DmlExecutionProvider", "CPUExecutionProvider"],  # needs onnxruntime-directml instead of onnxruntime-gpu
    "cpu": ["CPUExecutionProvider"],
}
CLIP_MEAN = np.array([0.48145466, 0.4578275, 0.40821073], dtype=np.float32)
CLIP_STD = np.array([0.26862954, 0.26130258, 0.27577711], dtype=np.float32)
IMAGENET_MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
IMAGENET_STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


# ---------------------------------------------------------------- preview extraction

class ExifTool:
    """exiftool -stay_open: one process, binary-safe responses terminated by {readyN}."""

    def __init__(self, exe: Path) -> None:
        self._process = subprocess.Popen([str(exe), "-stay_open", "True", "-@", "-"],
                                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self._counter = 0

    def binary_tag(self, path: Path, tag: str) -> bytes:
        self._counter += 1
        marker = f"{{ready{self._counter}}}".encode()
        command = f"-b\n-{tag}\n{path}\n-execute{self._counter}\n"
        self._process.stdin.write(command.encode("utf-8"))
        self._process.stdin.flush()
        buffer = bytearray()
        while not buffer.rstrip().endswith(marker):
            chunk = self._process.stdout.read1(1 << 16)
            if not chunk:
                raise RuntimeError("exiftool terminated")
            buffer += chunk
        data = bytes(buffer.rstrip())
        return data[: -len(marker)]

    def close(self) -> None:
        self._process.stdin.write(b"-stay_open\nFalse\n")
        self._process.stdin.flush()
        self._process.wait(timeout=10)


def load_preview(path: Path, exiftool: ExifTool) -> tuple[Image.Image, str]:
    if path.suffix.lower() in lc.RAW_EXTENSIONS:
        data, source = exiftool.binary_tag(path, "JpgFromRaw"), "JpgFromRaw"
        if len(data) < 1000:
            data, source = exiftool.binary_tag(path, "PreviewImage"), "PreviewImage"
        image = Image.open(io.BytesIO(data))
    else:
        image, source = Image.open(path), "file"
    if image.format == "JPEG":
        image.draft("RGB", (PREVIEW_LONG_SIDE, PREVIEW_LONG_SIDE))  # DCT-domain downscale, as a real decoder would
    image = image.convert("RGB")
    image.thumbnail((PREVIEW_LONG_SIDE, PREVIEW_LONG_SIDE), Image.Resampling.BILINEAR)
    return image, source


# ---------------------------------------------------------------- model stages

def to_nchw(array: np.ndarray) -> np.ndarray:
    return np.ascontiguousarray(array.transpose(2, 0, 1)[None, ...], dtype=np.float32)


class Stage:
    name = ""

    def __init__(self, testdata: Path, provider: list[str]) -> None:
        self.provider = provider

    def session(self, path: Path) -> ort.InferenceSession:
        options = ort.SessionOptions()
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        if "DmlExecutionProvider" in self.provider:
            # Required by the DirectML execution provider.
            options.enable_mem_pattern = False
            options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        return ort.InferenceSession(str(path), options, providers=self.provider)

    def run(self, image: Image.Image) -> dict:
        raise NotImplementedError


class DFineStage(Stage):
    def __init__(self, testdata: Path, provider: list[str], variant: str) -> None:
        super().__init__(testdata, provider)
        self.name = f"dfine-{variant}"
        self.sess = self.session(testdata / "models" / f"dfine-{variant}-obj2coco" / "model.onnx")

    def run(self, image: Image.Image) -> dict:
        x = to_nchw(np.asarray(image.resize((640, 640), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0)
        started = time.perf_counter()
        self.sess.run(None, {"pixel_values": x})
        return {"inference_ms": (time.perf_counter() - started) * 1000}


class ClipStage(Stage):
    name = "clip"

    def __init__(self, testdata: Path, provider: list[str]) -> None:
        super().__init__(testdata, provider)
        self.sess = self.session(testdata / "models" / "clip-vit-b32" / "vision_model.onnx")

    def run(self, image: Image.Image) -> dict:
        w, h = image.size
        scale = 224 / min(w, h)
        resized = image.resize((max(224, round(w * scale)), max(224, round(h * scale))), Image.Resampling.BICUBIC)
        left, top = (resized.width - 224) // 2, (resized.height - 224) // 2
        crop = np.asarray(resized.crop((left, top, left + 224, top + 224)), dtype=np.float32) / 255.0
        x = to_nchw((crop - CLIP_MEAN) / CLIP_STD)
        started = time.perf_counter()
        self.sess.run(None, {"pixel_values": x})
        return {"inference_ms": (time.perf_counter() - started) * 1000}


class RamPlusStage(Stage):
    name = "ramplus"

    def __init__(self, testdata: Path, provider: list[str]) -> None:
        super().__init__(testdata, provider)
        self.sess = self.session(testdata / "models" / "ramplus-fp32" / "ram_plus.onnx")
        self.input = self.sess.get_inputs()[0].name

    def run(self, image: Image.Image) -> dict:
        x = np.asarray(image.resize((384, 384), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
        x = to_nchw((x - IMAGENET_MEAN) / IMAGENET_STD)
        started = time.perf_counter()
        self.sess.run(None, {self.input: x})
        return {"inference_ms": (time.perf_counter() - started) * 1000}


class FacesStage(Stage):
    """buffalo_l: SCRFD detection, then 106 landmarks and embedding for each face."""

    name = "faces"
    strides = (8, 16, 32)

    def __init__(self, testdata: Path, provider: list[str]) -> None:
        super().__init__(testdata, provider)
        folder = testdata / "models" / "insightface-buffalo-l"
        self.det = self.session(folder / "det_10g.onnx")
        self.lmk = self.session(folder / "2d106det.onnx")
        self.rec = self.session(folder / "w600k_r50.onnx")
        self.det_input = self.det.get_inputs()[0].name
        self.lmk_input = self.lmk.get_inputs()[0].name
        self.rec_input = self.rec.get_inputs()[0].name

    def detect(self, image: Image.Image) -> list[tuple[float, float, float, float]]:
        scale = 640 / max(image.size)
        resized = image.resize((round(image.width * scale), round(image.height * scale)), Image.Resampling.BILINEAR)
        canvas = np.zeros((640, 640, 3), dtype=np.float32)
        canvas[: resized.height, : resized.width] = np.asarray(resized, dtype=np.float32)
        outputs = self.det.run(None, {self.det_input: to_nchw((canvas - 127.5) / 128.0)})
        boxes, scores = [], []
        for i, stride in enumerate(self.strides):
            score = outputs[i].reshape(-1)
            distance = outputs[i + 3].reshape(-1, 4) * stride
            size = 640 // stride
            ys, xs = np.mgrid[:size, :size]
            centers = np.stack([xs, ys], axis=-1).reshape(-1, 2) * stride
            centers = np.repeat(centers, 2, axis=0)  # 2 anchors per location
            keep = score >= 0.5
            if keep.any():
                c, d = centers[keep], distance[keep]
                boxes.append(np.stack([c[:, 0] - d[:, 0], c[:, 1] - d[:, 1], c[:, 0] + d[:, 2], c[:, 1] + d[:, 3]], axis=1) / scale)
                scores.append(score[keep])
        if not boxes:
            return []
        return nms(np.concatenate(boxes), np.concatenate(scores), 0.4)

    def run(self, image: Image.Image) -> dict:
        started = time.perf_counter()
        faces = self.detect(image)
        for x1, y1, x2, y2 in faces:
            crop = image.crop((int(x1), int(y1), int(x2), int(y2)))
            lmk = np.asarray(crop.resize((192, 192), Image.Resampling.BILINEAR), dtype=np.float32)
            self.lmk.run(None, {self.lmk_input: to_nchw(lmk)})
            rec = np.asarray(crop.resize((112, 112), Image.Resampling.BILINEAR), dtype=np.float32)
            self.rec.run(None, {self.rec_input: to_nchw((rec - 127.5) / 127.5)})
        return {"inference_ms": (time.perf_counter() - started) * 1000, "faces": len(faces)}


def nms(boxes: np.ndarray, scores: np.ndarray, threshold: float) -> list[tuple[float, float, float, float]]:
    order = scores.argsort()[::-1]
    keep = []
    while order.size:
        i = order[0]
        keep.append(i)
        xx1 = np.maximum(boxes[i, 0], boxes[order[1:], 0])
        yy1 = np.maximum(boxes[i, 1], boxes[order[1:], 1])
        xx2 = np.minimum(boxes[i, 2], boxes[order[1:], 2])
        yy2 = np.minimum(boxes[i, 3], boxes[order[1:], 3])
        inter = np.clip(xx2 - xx1, 0, None) * np.clip(yy2 - yy1, 0, None)
        area = lambda b: (b[:, 2] - b[:, 0]) * (b[:, 3] - b[:, 1])  # noqa: E731
        iou = inter / (area(boxes[[i]]) + area(boxes[order[1:]]) - inter + 1e-9)
        order = order[1:][iou <= threshold]
    return [tuple(map(float, boxes[i])) for i in keep]


# ---------------------------------------------------------------- reporting

def summarize(values: list[float]) -> dict:
    ordered = sorted(values)
    p95 = ordered[min(len(ordered) - 1, int(round(0.95 * (len(ordered) - 1))))]
    mean = statistics.fmean(values)
    return {"n": len(values), "mean_ms": round(mean, 2), "p50_ms": round(statistics.median(values), 2),
            "p95_ms": round(p95, 2), "photos_per_s": round(1000 / mean, 2) if mean else None}


def write_report(out_dir: Path, report: dict) -> None:
    (out_dir / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    lines = [
        f"# PhotoFlow benchmark — {report['started']}",
        "",
        f"Photos: {report['photos']['count']} ({', '.join(f'{k} {v}' for k, v in report['photos']['by_extension'].items())}); "
        f"preview sources: {', '.join(f'{k} {v}' for k, v in report['photos']['preview_sources'].items())}",
        "",
        "Environment: " + ", ".join(f"{k} {v}" for k, v in report["environment"].items()),
        "",
        "| Stage | Provider | Mean ms/photo | p50 | p95 | Photos/s | Notes |",
        "|---|---|---|---|---|---|---|",
    ]
    for row in report["stages"]:
        s = row["summary"]
        lines.append(f"| {row['stage']} | {row['provider']} | {s['mean_ms']} | {s['p50_ms']} | {s['p95_ms']} | {s['photos_per_s']} | {row.get('notes', '')} |")
    lines += ["", "## Estimate for 1,000 photos (preview + detection + faces + CLIP + RAM++)", "",
              "| Configuration | Minutes | Spec §5 estimate |", "|---|---|---|"]
    for est in report["estimates"]:
        lines.append(f"| {est['configuration']} | {est['minutes']} | {est['spec']} |")
    lines += ["", "Excluded: copy and verification, XMP writing, groupings and decisions, OCR, captions. "
              "Model stages run sequentially on a single stream; the app pipeline overlaps stages."]
    (out_dir / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=200)
    parser.add_argument("--providers", nargs="+", choices=sorted(PROVIDERS), default=["cuda", "cpu"])
    parser.add_argument("--stages", nargs="+", default=["dfine-s", "dfine-m", "faces", "clip", "ramplus"])
    args = parser.parse_args()

    testdata = lc.testdata_root()
    photos_dir = args.photos.resolve()
    lc.ensure_outside_repo(photos_dir)
    photos = lc.list_images(photos_dir, include_raw=True)[: args.limit]
    if not photos:
        raise SystemExit(f"No photos in {photos_dir}")
    out_dir = lc.output_dir("lab", "benchmark", datetime.now().strftime("%Y%m%d-%H%M%S"))
    report = {"started": datetime.now().isoformat(timespec="seconds"), "environment": lc.environment_info(),
              "photos": {"count": len(photos), "by_extension": dict(Counter(p.suffix.lower() for p in photos))},
              "stages": [], "estimates": []}

    print(f"Preview extraction: {len(photos)} photos")
    exiftool = ExifTool(lc.ensure_exiftool())
    previews, preview_ms, sources = [], [], Counter()
    try:
        for path in photos:
            started = time.perf_counter()
            image, source = load_preview(path, exiftool)
            preview_ms.append((time.perf_counter() - started) * 1000)
            previews.append(image)
            sources[source] += 1
    finally:
        exiftool.close()
    report["photos"]["preview_sources"] = dict(sources)
    report["stages"].append({"stage": "preview", "provider": "cpu", "summary": summarize(preview_ms),
                             "notes": f"long side {PREVIEW_LONG_SIDE}"})

    factories = {
        "dfine-s": lambda p: DFineStage(testdata, p, "s"),
        "dfine-m": lambda p: DFineStage(testdata, p, "m"),
        "faces": lambda p: FacesStage(testdata, p),
        "clip": lambda p: ClipStage(testdata, p),
        "ramplus": lambda p: RamPlusStage(testdata, p),
    }
    means: dict[tuple[str, str], float] = {}
    for provider_name in args.providers:
        for stage_name in args.stages:
            stage = factories[stage_name](PROVIDERS[provider_name])
            active = stage.sess.get_providers()[0] if hasattr(stage, "sess") else stage.det.get_providers()[0]
            for _ in range(WARMUP):
                stage.run(previews[0])
            timings, faces = [], []
            for image in previews:
                result = stage.run(image)
                timings.append(result["inference_ms"])
                faces.append(result.get("faces", 0))
            summary = summarize(timings)
            means[(stage_name, provider_name)] = summary["mean_ms"]
            notes = f"active: {active}"
            if stage_name == "faces":
                notes += f"; faces/photo {statistics.fmean(faces):.2f}"
            report["stages"].append({"stage": stage_name, "provider": provider_name, "summary": summary, "notes": notes})
            print(f"  {stage_name:>8} {provider_name:>4}: {summary['mean_ms']:8.1f} ms/photo  ({notes})")

    preview_mean = statistics.fmean(preview_ms)
    for configuration, detector, provider, spec in (("GPU (CUDA), D-FINE M", "dfine-m", "cuda", "~6-11 min (RTX 3090)"),
                                                     ("CPU only, D-FINE S", "dfine-s", "cpu", "~22-50 min (8 cores)")):
        keys = [(detector, provider), ("faces", provider), ("clip", provider), ("ramplus", provider)]
        if all(k in means for k in keys):
            total_ms = preview_mean + sum(means[k] for k in keys)
            report["estimates"].append({"configuration": configuration, "minutes": round(total_ms * 1000 / 60000, 1), "spec": spec})

    write_report(out_dir, report)
    print(f"Report: {out_dir / 'report.md'}")


if __name__ == "__main__":
    main()
