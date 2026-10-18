# PhotoFlow lab

Python tools used only during development (spec D1): benchmarks, ONNX export and parity
references. The application itself never runs Python.

**Every output goes to `PHOTOFLOW_TESTDATA`**, outside the repository: models, references,
reports, caches. The scripts refuse to write inside the repository.

## Setup

Requires [uv](https://docs.astral.sh/uv/) and Python 3.12. CUDA wheels (torch cu130,
onnxruntime-gpu 1.31) need an NVIDIA driver supporting CUDA 13.

```bash
cd tools/lab
uv sync                 # base tools
uv sync --extra ram     # + original RAM++ (PyTorch) for verify_ramplus.py
```

## Scripts

| Script | Purpose | Output (under `PHOTOFLOW_TESTDATA`) |
|---|---|---|
| `fetch_models.py` | Download the models of `models/manifest.json`, SHA-256 verified. Models that require license acceptance (buffalo_l) only with `--accept-license <id>` | `models/<id>/` |
| `export_dfine.py` | Export D-FINE S/M (Objects365→COCO) to ONNX with the Hugging Face I/O contract | `models/dfine-{s,m}-obj2coco-export/` |
| `parity_refs.py --images <dir>` | Parity references from the original PyTorch D-FINE (input tensor, raw outputs, official detections) + first ONNX check | `parity/dfine-{s,m}/` |
| `benchmark.py --photos <dir>` | Per-stage timings on CUDA and CPU (preview, D-FINE S/M, faces, CLIP, RAM++) and estimate for 1,000 photos | `lab/benchmark/<timestamp>/` |
| `verify_ramplus.py --images <dir>` | Community RAM++ int8 ONNX vs original PyTorch RAM++: probabilities and tag sets | `lab/ramplus/<timestamp>/` |
| `florence_refs.py --photos <dir>` | Florence-2 caption references from the original PyTorch model (native transformers, run with `uv run --with "transformers==4.57.1"`): input tensor, generated token ids, caption | `parity/florence/` |
| `export_florence_vision.py` | Export of the Florence-2 vision encoder from the original weights (the onnx-community one does not match), checked against PyTorch; run like `florence_refs.py` | `models/florence2-base-ft/vision_encoder_pf.onnx` |
| `adaface_refs.py --snapshot <dir>` | AdaFace IR-101 fingerprint references from the authors' own CVLFace network (its `models/iresnet/model.py` and `model.safetensors` from the pinned snapshot) on the ArcFace crops of `face_embed_refs.py`; run with the Python of the conversion environment or `uv run --script` | `parity/faces/*.ada_emb.bin`, `parity/faces/adaface_index.json` |

`exiftool` for the benchmark is downloaded once (13.59, SHA-256 verified) into `tools/`.

## C# parity test

After `parity_refs.py`, run the local parity tests:

```bash
dotnet test --filter Category=Parity
```

Reports are written to `PHOTOFLOW_TESTDATA/parity/reports/`.

## Licenses

buffalo_l (InsightFace) is licensed for non-commercial research only: it is downloaded only
after explicit acceptance and never redistributed. All other models are listed with their
license in `models/manifest.json`.
