# SPDX-License-Identifier: Apache-2.0
"""Export D-FINE (S and M, Objects365 -> COCO) to ONNX with the Hugging Face I/O contract.

    pixel_values float32 [N,3,640,640] -> logits [N,300,80], pred_boxes [N,300,4] (cxcywh, normalized)

This is the same contract as the onnx-community files listed in models/manifest.json: our export is
the reference for the parity test and the fallback distribution (decision A4).

Usage:
    uv run python export_dfine.py            # S and M
    uv run python export_dfine.py --variant s

Output: PHOTOFLOW_TESTDATA/models/dfine-{s,m}-obj2coco-export/model.onnx + export_report.json
"""

from __future__ import annotations

import argparse
import json
import time

import numpy as np
import onnx
import onnxruntime as ort
import torch
import transformers
from transformers import DFineForObjectDetection

import labcommon as lc

CHECKPOINTS = {
    "s": ("ustc-community/dfine-small-obj2coco", "2b548d12a7623d674bb2aa2bf24022ce090b7736"),
    "m": ("ustc-community/dfine-medium-obj2coco", "e82849660b18b6eab3fbcc88c9560d844d919ab4"),
}
OPSET = 17
INPUT_SIZE = 640


class OutputsAsTuple(torch.nn.Module):
    """Exposes logits and pred_boxes as plain outputs with stable names."""

    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, pixel_values: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        outputs = self.model(pixel_values=pixel_values)
        return outputs.logits, outputs.pred_boxes


def export(variant: str) -> dict:
    repo, revision = CHECKPOINTS[variant]
    out_dir = lc.output_dir("models", f"dfine-{variant}-obj2coco-export")
    onnx_path = out_dir / "model.onnx"
    cache = lc.output_dir("hf-cache")

    model = DFineForObjectDetection.from_pretrained(repo, revision=revision, cache_dir=cache).eval()
    wrapper = OutputsAsTuple(model).eval()
    dummy = torch.rand(1, 3, INPUT_SIZE, INPUT_SIZE, generator=torch.Generator().manual_seed(0))

    started = time.perf_counter()
    exporter = "torchscript"
    try:
        torch.onnx.export(
            wrapper, (dummy,), str(onnx_path), dynamo=False, opset_version=OPSET,
            input_names=["pixel_values"], output_names=["logits", "pred_boxes"],
            dynamic_axes={"pixel_values": {0: "batch"}, "logits": {0: "batch"}, "pred_boxes": {0: "batch"}},
        )
    except Exception as error:  # noqa: BLE001 - fall back to the dynamo exporter if TorchScript is unavailable
        print(f"  TorchScript exporter failed ({type(error).__name__}: {error}); retrying with dynamo")
        exporter = "dynamo"
        torch.onnx.export(
            wrapper, (dummy,), str(onnx_path), dynamo=True, opset_version=max(OPSET, 18),
            input_names=["pixel_values"], output_names=["logits", "pred_boxes"],
            dynamic_shapes={"pixel_values": {0: torch.export.Dim("batch", max=16)}},
        )
    export_seconds = time.perf_counter() - started

    onnx.checker.check_model(str(onnx_path))

    # Smoke check on the tracing input only. Synthetic inputs (noise, flat gray) produce near-tied encoder
    # scores, so the top-k query order may legitimately differ between runtimes and raw outputs diverge:
    # the meaningful comparison runs on real images in parity_refs.py (python_check.json) and in the C# test.
    checks = []
    for name, sample in (("trace-input", dummy),):
        with torch.no_grad():
            ref_logits, ref_boxes = wrapper(sample)
        session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
        logits, boxes = session.run(["logits", "pred_boxes"], {"pixel_values": sample.numpy()})
        checks.append({
            "input": name,
            "max_abs_diff_logits": float(np.abs(logits - ref_logits.numpy()).max()),
            "max_abs_diff_boxes": float(np.abs(boxes - ref_boxes.numpy()).max()),
        })

    report = {
        "variant": variant,
        "checkpoint": repo,
        "revision": revision,
        "exporter": exporter,
        "opset": onnx.load(str(onnx_path), load_external_data=False).opset_import[0].version,
        "file": str(onnx_path),
        "size": onnx_path.stat().st_size,
        "sha256": lc.sha256_of(onnx_path),
        "export_seconds": round(export_seconds, 1),
        "smoke_check_vs_pytorch": checks,
        "versions": {"torch": torch.__version__, "transformers": transformers.__version__,
                     "onnx": onnx.__version__, "onnxruntime": ort.__version__},
    }
    (out_dir / "export_report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--variant", choices=sorted(CHECKPOINTS), action="append")
    args = parser.parse_args()
    for variant in args.variant or sorted(CHECKPOINTS, reverse=True):
        print(f"D-FINE {variant.upper()}")
        report = export(variant)
        for check in report["smoke_check_vs_pytorch"]:
            print(f"  smoke ({check['input']}): max |Δ| logits {check['max_abs_diff_logits']:.2e}, boxes {check['max_abs_diff_boxes']:.2e}")
        print(f"  {report['file']}  ({report['size'] / 1e6:.1f} MB, sha256 {report['sha256'][:16]}…, {report['exporter']}, opset {report['opset']})")


if __name__ == "__main__":
    main()
