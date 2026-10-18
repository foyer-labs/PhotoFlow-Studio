# SPDX-License-Identifier: Apache-2.0
"""Parity references for the CLIP image embedder (spec §5, §14: embedding similarity >= 0.999).

Reference: the original openai/clip-vit-base-patch32 in PyTorch (transformers CLIPModel.get_image_features) with
the official CLIPImageProcessor. The ONNX conversions of Xenova (fp32 and dynamically quantized) are compared with
it on the same preprocessed tensor, so the comparison isolates the model; the C# test then checks its own
preprocessing (Pillow bicubic resize of the shortest side to 224, center crop, normalization) against the saved
tensor and the C# embedding against the saved reference.

Per photo (stem): <stem>.rgb (analysis image, RGB bytes, long side <= 2048; the C# input), <stem>.clip_input.bin
(float32 [1,3,224,224]), <stem>.clip_ref.bin (float32 [512], L2-normalized PyTorch embedding).
index.json: sizes and, per variant (fp32, quantized), the cosine with the reference.
Also writes text references for a few prompts (token ids and normalized text embeddings) for the tokenizer and
text-encoder parity.

Usage:
    uv run python clip_refs.py --photos E:/path/outside/repo/photos [--limit 40]
Output: PHOTOFLOW_TESTDATA/parity/clip/
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from PIL import Image
from transformers import CLIPModel, CLIPProcessor

import labcommon as lc

REFERENCE = "openai/clip-vit-base-patch32"
REFERENCE_REVISION = "3d74acf9a28c67741b2f4f2ea7635f0aaf6f0268"
XENOVA_REVISION = "d15189d7028b43f1d3e65039190477f6af591c2a"
FP32_URL = f"https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/{XENOVA_REVISION}/onnx/vision_model.onnx"
FP32_TEXT_URL = f"https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/{XENOVA_REVISION}/onnx/text_model.onnx"
PROMPTS = [
    "a photo of a wedding",
    "a portrait of a person",
    "a racing car on a track",
    "a photo of a dog",
    "a landscape with mountains",
    "the northern lights at night",
    "una foto di gruppo al tramonto",
]


def analysis_image(image: Image.Image) -> Image.Image:
    scale = 2048 / max(image.size)
    if scale >= 1:
        return image
    return image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))), Image.Resampling.BILINEAR)


def fetch(url: str, name: str) -> Path:
    """Downloads once (no pinned hash yet: printed so that the manifest can record it)."""
    import urllib.request

    target = lc.output_dir("models", "clip-vit-b32") / name
    if not target.exists():
        partial = target.with_name(name + ".part")
        request = urllib.request.Request(url, headers={"User-Agent": "photoflow-lab"})
        with urllib.request.urlopen(request) as response, partial.open("wb") as out:
            while chunk := response.read(1 << 20):
                out.write(chunk)
        partial.replace(target)
    print(f"{name}: {target.stat().st_size} bytes, sha256 {lc.sha256_of(target)}")
    return target


def cosine(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=40)
    args = parser.parse_args()
    photos = args.photos.resolve()
    lc.ensure_outside_repo(photos)
    out = lc.output_dir("parity", "clip")

    model = CLIPModel.from_pretrained(REFERENCE, revision=REFERENCE_REVISION).eval()
    processor = CLIPProcessor.from_pretrained(REFERENCE, revision=REFERENCE_REVISION)
    variants = {
        "fp32": fetch(FP32_URL, "vision_model.onnx"),
        "quantized": lc.model_file("clip-vit-b32-quantized", "vision_model_quantized.onnx"),
    }
    sessions = {
        name: ort.InferenceSession(str(path), providers=["CPUExecutionProvider"]) for name, path in variants.items()
    }
    gpu = ort.InferenceSession(str(variants["fp32"]), providers=["CUDAExecutionProvider", "CPUExecutionProvider"])

    index = {"reference": f"{REFERENCE}@{REFERENCE_REVISION}", "photos": []}
    for path in lc.list_images(photos, include_raw=True)[: args.limit]:
        image = analysis_image(lc.load_photo(path))
        (out / f"{path.stem}.rgb").write_bytes(np.asarray(image, dtype=np.uint8).tobytes())
        pixel = processor(images=image, return_tensors="pt")["pixel_values"]
        with torch.no_grad():
            reference = model.get_image_features(pixel_values=pixel)[0].numpy()
        reference = reference / np.linalg.norm(reference)
        tensor = pixel.numpy().astype(np.float32)
        tensor.tofile(out / f"{path.stem}.clip_input.bin")
        reference.astype(np.float32).tofile(out / f"{path.stem}.clip_ref.bin")
        entry = {"stem": path.stem, "width": image.width, "height": image.height}
        for name, session in [*sessions.items(), ("fp32-cuda", gpu)]:
            embeds = session.run(["image_embeds"], {"pixel_values": tensor})[0][0]
            entry[name] = cosine(embeds, reference)
        index["photos"].append(entry)
        print(f"{path.name}: " + ", ".join(f"{k} {entry[k]:.6f}" for k in ("fp32", "fp32-cuda", "quantized")))

    text_session = ort.InferenceSession(str(fetch(FP32_TEXT_URL, "text_model.onnx")), providers=["CPUExecutionProvider"])
    texts = []
    for prompt in PROMPTS:
        tokens = processor.tokenizer(prompt, padding="max_length", max_length=77, return_tensors="pt")
        with torch.no_grad():
            reference = model.get_text_features(**tokens)[0].numpy()
        reference = reference / np.linalg.norm(reference)
        ids = tokens["input_ids"][0].tolist()
        onnx = text_session.run(["text_embeds"], {"input_ids": tokens["input_ids"].numpy().astype(np.int64)})[0][0]
        texts.append({"prompt": prompt, "ids": ids, "embedding": reference.tolist(), "onnx_cosine": cosine(onnx, reference)})
        print(f"text '{prompt}': onnx fp32 {texts[-1]['onnx_cosine']:.6f}")
    index["texts"] = texts

    for name in ("fp32", "fp32-cuda", "quantized"):
        values = [p[name] for p in index["photos"]]
        index[f"min_{name}"] = min(values)
        print(f"{name}: min cosine {min(values):.6f}, mean {np.mean(values):.6f}")
    (out / "index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
