# SPDX-License-Identifier: Apache-2.0
"""Export of the Florence-2 vision encoder to ONNX (decision F5-1).

The vision encoder of onnx-community/Florence-2-base-ft does not match the original model (features cosine 0.94,
max difference 6 on values up to 10; the text encoder and the decoder of that conversion match within 2e-4). This
script exports `get_image_features` of the original weights (florence-community/Florence-2-base-ft, MIT, pinned) and
checks it against PyTorch on the parity photos.

Usage:
    uv run --with "transformers==4.57.1" python export_florence_vision.py
Output: PHOTOFLOW_TESTDATA/models/florence2-base-ft/vision_encoder_pf.onnx (+ export.json)
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from PIL import Image

import labcommon as lc

MODEL = "florence-community/Florence-2-base-ft"
REVISION = "0b03b6f15a4a211370fb204aee4e7dd48887ea37"
OPSET = 17
FILE = "vision_encoder_pf.onnx"


class VisionEncoder(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
        return self.model.get_image_features(pixel_values)


def main() -> None:
    from transformers import AutoProcessor, Florence2ForConditionalGeneration

    cache = str(lc.testdata_root() / "hf-cache")
    processor = AutoProcessor.from_pretrained(MODEL, revision=REVISION, cache_dir=cache)
    model = Florence2ForConditionalGeneration.from_pretrained(MODEL, revision=REVISION, cache_dir=cache, dtype=torch.float32,
                                                              attn_implementation="eager").eval()
    wrapper = VisionEncoder(model).eval()
    out_dir = lc.model_dir("florence2-base-ft")
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / FILE
    dummy = torch.zeros(1, 3, 768, 768)
    with torch.no_grad():
        torch.onnx.export(wrapper, (dummy,), str(path), dynamo=False, opset_version=OPSET, input_names=["pixel_values"],
                          output_names=["image_features"], dynamic_axes={"pixel_values": {0: "batch"}, "image_features": {0: "batch"}})

    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    refs = lc.testdata_root() / "parity" / "florence"
    index = json.loads((refs / "index.json").read_text(encoding="utf-8"))
    worst = 0.0
    for entry in index["images"]:
        image = Image.frombytes("RGB", (entry["width"], entry["height"]), (refs / f"{entry['stem']}.rgb").read_bytes())
        pixels = processor(text="<DETAILED_CAPTION>", images=image, return_tensors="pt")["pixel_values"]
        with torch.no_grad():
            expected = wrapper(pixels).numpy()
        actual = session.run(None, {"pixel_values": pixels.numpy()})[0]
        worst = max(worst, float(np.abs(expected - actual).max()))
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    report = {"model": MODEL, "revision": REVISION, "opset": OPSET, "file": FILE, "size": path.stat().st_size, "sha256": digest,
              "max_difference": worst, "images": len(index["images"]), "torch": torch.__version__, "onnxruntime": ort.__version__}
    (out_dir / "export.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
    print(json.dumps(report, indent=1))


if __name__ == "__main__":
    main()
