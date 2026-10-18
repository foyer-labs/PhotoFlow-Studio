# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = [
#     "torch==2.14.1",
#     "safetensors==0.8.0",
#     "numpy==2.5.4",
# ]
# ///
# SPDX-License-Identifier: Apache-2.0
"""Parity references for the AdaFace IR-101 fingerprints (P22): the authors' own network on the aligned crops.

The network is the one of the CVLFace snapshot (minchul/cvlface_adaface_ir101_webface12m, pinned revision in
models/manifest.json): its models/iresnet/model.py is loaded from the snapshot folder given on the command line (only
fvcore's FLOP counter, which the forward pass does not use, is replaced by a stub) with the weights of model.safetensors
(strict). It runs in float32 on the CPU on the ArcFace crops of face_embed_refs.py (the same alignment: run that first),
with CVLFace's test transform: RGB, (x / 255 − 0.5) / 0.5.

Per photo (stem) and face k:
  <stem>.f<k>.ada_emb.bin  float32 [512], raw output
adaface_index.json: versions and the faces.

Usage:
    uv run --script adaface_refs.py --snapshot <folder of the CVLFace snapshot (config.json, model.safetensors, models/)>
Output: PHOTOFLOW_TESTDATA/parity/faces/ (next to the face_embed_refs.py references)
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import types
from pathlib import Path

import numpy as np
import torch
from safetensors.torch import load_file

import labcommon as lc


def load_network(snapshot: Path) -> torch.nn.Module:
    sys.modules.setdefault("fvcore", types.ModuleType("fvcore"))
    fvcore_nn = types.ModuleType("fvcore.nn")
    fvcore_nn.flop_count = None  # only the FLOP counter of the authors' module, never called here
    sys.modules.setdefault("fvcore.nn", fvcore_nn)
    spec = importlib.util.spec_from_file_location("cvlface_iresnet", snapshot / "models" / "iresnet" / "model.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    network = module.IR_101(input_size=(112, 112), output_dim=512)
    state = load_file(str(snapshot / "model.safetensors"))
    prefix = next(p for p in ("model.net.", "net.", "") if any(k.startswith(p) for k in state))
    network.load_state_dict({k[len(prefix):]: v for k, v in state.items() if k.startswith(prefix)}, strict=True)
    return network.eval()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--snapshot", type=Path, required=True)
    args = parser.parse_args()
    folder = lc.output_dir("parity", "faces")
    index = json.loads((folder / "embed_index.json").read_text(encoding="utf-8"))
    network = load_network(args.snapshot.resolve())

    faces = []
    with torch.no_grad():
        for entry in index["faces"]:
            stem, k = entry["image"], entry["face"]
            crop = np.fromfile(folder / f"{stem}.f{k}.arc_crop.rgb", dtype=np.uint8).reshape(112, 112, 3)
            x = torch.from_numpy(crop.astype(np.float32) / 255.0).permute(2, 0, 1).unsqueeze(0)
            embedding = network((x - 0.5) / 0.5)[0].numpy()
            embedding.astype(np.float32).tofile(folder / f"{stem}.f{k}.ada_emb.bin")
            faces.append({"image": stem, "face": k})

    out = {"torch": torch.__version__, "snapshot": str(args.snapshot.name), "faces": faces}
    (folder / "adaface_index.json").write_text(json.dumps(out, indent=1), encoding="utf-8")
    print(f"{len(faces)} faces, torch {torch.__version__}")


if __name__ == "__main__":
    main()
