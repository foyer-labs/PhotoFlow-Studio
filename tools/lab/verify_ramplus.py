# SPDX-License-Identifier: Apache-2.0
"""Verify a RAM++ ONNX export (int8 or fp32) against the original PyTorch RAM++ ("verifica RAM++", spec §13).

Both models receive the same input tensor (official transform: 384x384 resize, ImageNet normalization), so
the comparison isolates the export/quantization. For each image: max |Δ probability| and the tag sets at the
official per-class thresholds (delete list applied to both), then Jaccard agreement over the set.

Requires the optional dependencies:  uv sync --extra ram
Downloads the original weights (3.0 GB, SHA-256 verified) into PHOTOFLOW_TESTDATA/hf-cache.

Usage:
    uv run --extra ram python verify_ramplus.py --images E:/path/outside/repo/photos [--model ramplus-fp32 --onnx ram_plus.onnx]
Output: PHOTOFLOW_TESTDATA/lab/ramplus/<timestamp>/report.md + report.json
"""

from __future__ import annotations

import argparse
import json
import statistics
from datetime import datetime
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
import torch.nn.functional as F
from huggingface_hub import hf_hub_download
from PIL import Image

import labcommon as lc

WEIGHTS_REPO = "xinyu1205/recognize-anything-plus-model"
WEIGHTS_REVISION = "84d4aee3a0265c4e0df1f714f0572011d1bf2ec3"
WEIGHTS_FILE = "ram_plus_swin_large_14m.pth"
WEIGHTS_SHA256 = "497c178836ba66698ca226c7895317e6e800034be986452dbd2593298d50e87d"


@torch.no_grad()
def ram_logits(model, image: torch.Tensor) -> torch.Tensor:
    """generate_tag() of ram/models/ram_plus.py up to the logits (before thresholding)."""
    image_embeds = model.image_proj(model.visual_encoder(image))
    image_atts = torch.ones(image_embeds.size()[:-1], dtype=torch.long, device=image.device)
    image_cls_embeds = image_embeds[:, 0, :]
    bs = image_embeds.shape[0]
    des_per_class = int(model.label_embed.shape[0] / model.num_class)
    image_cls_embeds = image_cls_embeds / image_cls_embeds.norm(dim=-1, keepdim=True)
    logits_per_image = (model.reweight_scale.exp() * image_cls_embeds @ model.label_embed.t()).view(bs, -1, des_per_class)
    weight_normalized = F.softmax(logits_per_image, dim=2)
    reshaped = model.label_embed.view(-1, des_per_class, 512)
    label_embed_reweight = (weight_normalized.unsqueeze(-1) * reshaped).sum(dim=2)
    label_embed = torch.nn.functional.relu(model.wordvec_proj(label_embed_reweight))
    tagging_embed = model.tagging_head(encoder_embeds=label_embed, encoder_hidden_states=image_embeds,
                                       encoder_attention_mask=image_atts, return_dict=False, mode="tagging")
    return model.fc(tagging_embed[0]).squeeze(-1)


def tag_set(probabilities: np.ndarray, thresholds: np.ndarray, deleted: set[int], tags: list[str]) -> set[str]:
    return {tags[i] for i in np.flatnonzero(probabilities > thresholds) if i not in deleted}


def main() -> None:
    from ram import get_transform
    from ram.models import ram_plus

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--images", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--model", default="ramplus-fp32", help="model folder under PHOTOFLOW_TESTDATA/models")
    parser.add_argument("--onnx", default="ram_plus.onnx", help="ONNX file in the model folder")
    parser.add_argument("--provider", default="CPUExecutionProvider")
    args = parser.parse_args()

    testdata = lc.testdata_root()
    images = lc.list_images(args.images.resolve())
    if args.limit:
        images = images[: args.limit]
    if not images:
        raise SystemExit(f"No images in {args.images}")

    weights = Path(hf_hub_download(WEIGHTS_REPO, WEIGHTS_FILE, revision=WEIGHTS_REVISION, cache_dir=lc.output_dir("hf-cache")))
    if lc.sha256_of(weights) != WEIGHTS_SHA256:
        raise SystemExit(f"SHA-256 mismatch for {weights}")

    device = "cuda" if torch.cuda.is_available() else "cpu"
    model = ram_plus(pretrained=str(weights), image_size=384, vit="swin_l").eval().to(device)
    transform = get_transform(image_size=384)
    thresholds = model.class_threshold.numpy()
    deleted = set(int(i) for i in getattr(model, "delete_tag_index", []))
    tags = list(model.tag_list)

    onnx_model = lc.model_file(args.model, args.onnx)
    session = ort.InferenceSession(str(onnx_model), providers=[args.provider])
    input_name = session.get_inputs()[0].name

    # The community files must describe the same tags/thresholds as the original package.
    onnx_tags = (lc.model_dir(args.model) / "ram_tag_list.txt").read_text(encoding="utf-8").splitlines()
    onnx_thresholds = np.array([float(v) for v in (lc.model_dir(args.model) / "ram_tag_list_threshold.txt").read_text(encoding="utf-8").split()])
    files_consistent = onnx_tags == tags and np.allclose(onnx_thresholds, thresholds)

    rows = []
    for path in images:
        tensor = transform(Image.open(path).convert("RGB")).unsqueeze(0)
        p_torch = torch.sigmoid(ram_logits(model, tensor.to(device))).float().cpu().numpy()[0]
        p_onnx = 1 / (1 + np.exp(-session.run(None, {input_name: tensor.numpy()})[0][0]))
        t_torch, t_onnx = tag_set(p_torch, thresholds, deleted, tags), tag_set(p_onnx, thresholds, deleted, tags)
        union = t_torch | t_onnx
        rows.append({
            "image": path.name,
            "max_abs_diff_probability": float(np.abs(p_torch - p_onnx).max()),
            "jaccard": len(t_torch & t_onnx) / len(union) if union else 1.0,
            "tags_pytorch": sorted(t_torch), "only_pytorch": sorted(t_torch - t_onnx), "only_onnx": sorted(t_onnx - t_torch),
        })
        print(f"  {path.name}: jaccard {rows[-1]['jaccard']:.3f}, max |Δp| {rows[-1]['max_abs_diff_probability']:.3f}, "
              f"-{len(rows[-1]['only_pytorch'])} +{len(rows[-1]['only_onnx'])}")

    report = {
        "started": datetime.now().isoformat(timespec="seconds"),
        "original": {"repo": WEIGHTS_REPO, "revision": WEIGHTS_REVISION, "file": WEIGHTS_FILE, "device": device},
        "onnx": {"file": str(onnx_model), "provider": args.provider},
        "tag_files_consistent_with_original": bool(files_consistent),
        "images": len(rows),
        "mean_jaccard": round(statistics.fmean(r["jaccard"] for r in rows), 4),
        "identical_sets": sum(1 for r in rows if not r["only_pytorch"] and not r["only_onnx"]),
        "max_abs_diff_probability": round(max(r["max_abs_diff_probability"] for r in rows), 4),
        "per_image": rows,
        "environment": lc.environment_info(),
    }
    out_dir = lc.output_dir("lab", "ramplus", datetime.now().strftime("%Y%m%d-%H%M%S"))
    (out_dir / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    lines = [f"# RAM++ ONNX ({args.model}/{args.onnx}) vs original PyTorch — {report['started']}", "",
             f"Images: {report['images']}; identical tag sets: {report['identical_sets']}; "
             f"mean Jaccard: {report['mean_jaccard']}; max |Δ probability|: {report['max_abs_diff_probability']}; "
             f"tag/threshold files consistent: {report['tag_files_consistent_with_original']}", "",
             "| Image | Jaccard | max Δp | Only PyTorch | Only ONNX |", "|---|---|---|---|---|"]
    lines += [f"| {r['image']} | {r['jaccard']:.3f} | {r['max_abs_diff_probability']:.3f} | {', '.join(r['only_pytorch'])} | {', '.join(r['only_onnx'])} |" for r in rows]
    (out_dir / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Report: {out_dir / 'report.md'}")


if __name__ == "__main__":
    main()
