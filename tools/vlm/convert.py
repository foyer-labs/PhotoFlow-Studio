# SPDX-License-Identifier: Apache-2.0
"""Conversions on the user's PC: the level D model for ONNX Runtime GenAI (phase 5b, decisions U2-U4: Qwen3.5-9B or
Qwen2.5-VL-7B), and the AdaFace IR-101 face recognizer to ONNX (P22, recipes/adaface_ir101, recognized by its config).

Runs Microsoft's Olive recipe of the model (recipes/<model_type>/, MIT), chosen from the model_type of the weights, on
the local folder of the original weights: embedding and vision encoder exported and optimized by Olive, text decoder
built as int4 by the ONNX Runtime GenAI Model Builder, then the GenAI runtime configuration. Only reads the weights;
writes the models in --output (a new folder).

Progress for PhotoFlow on stdout: "PF-PROGRESS <step>/<steps> <name>", then "PF-DONE" or "PF-ERROR <message>".

Usage:
    python convert.py --weights <weights folder> --output <models folder> --work <working folder>
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
PARTS = ("embedding", "text", "vision")

# model_type of the weights → folder of its recipe.
RECIPES = {"qwen3_5": "qwen3_5", "qwen2_5_vl": "qwen2_5_vl"}


def progress(step: int, steps: int, name: str) -> None:
    print(f"PF-PROGRESS {step}/{steps} {name}", flush=True)


def prepare_onnxruntime() -> None:
    """CUDA libraries from torch first; only the CUDA provider has to register (Olive tries every one the package has,
    TensorRT included, which needs a TensorRT installation PhotoFlow does not use)."""
    import torch  # noqa: F401, PLC0415 - loads the CUDA and cuDNN libraries shipped with torch
    import onnxruntime as ort  # noqa: PLC0415

    if hasattr(ort, "preload_dlls"):
        ort.preload_dlls()
    register = ort.register_execution_provider_library

    def register_needed(name: str, path: str) -> None:
        try:
            register(name, path)
        except Exception as error:  # noqa: BLE001 - only CUDA is needed
            if name == "CUDAExecutionProvider" or "already registered" in str(error):
                raise
            print(f"PF-NOTE {name} not available ({type(error).__name__}); not needed", flush=True)

    ort.register_execution_provider_library = register_needed


def check_weights(weights: Path) -> str:
    """The recipe of the weights; they must be complete."""
    config = json.loads((weights / "config.json").read_text(encoding="utf-8"))
    model_type = config.get("model_type")
    if model_type not in RECIPES:
        raise ValueError(f"{weights} does not hold Qwen3.5 or Qwen2.5-VL weights (model_type {model_type!r}).")
    index = json.loads((weights / "model.safetensors.index.json").read_text(encoding="utf-8"))
    missing = sorted({f for f in index["weight_map"].values() if not (weights / f).exists()})
    if missing:
        raise FileNotFoundError(f"Missing weight files: {', '.join(missing)}")
    return RECIPES[model_type]


def check_embedding(models: Path, weights: Path) -> None:
    """The exported embedding must give the checkpoint's own vectors (a weight left at random only warns in transformers)."""
    import numpy as np  # noqa: PLC0415
    import onnxruntime as ort  # noqa: PLC0415
    from safetensors import safe_open  # noqa: PLC0415

    index = json.loads((weights / "model.safetensors.index.json").read_text(encoding="utf-8"))["weight_map"]
    key = next(k for k in ("model.embed_tokens.weight", "model.language_model.embed_tokens.weight") if k in index)
    ids = np.array([[0, 1000, 151644, 9707]], dtype=np.int64)
    with safe_open(str(weights / index[key]), "pt") as f:
        expected = f.get_slice(key)[ids[0].tolist()].float().numpy()
    session = ort.InferenceSession(str(models / "embedding.onnx"), providers=["CPUExecutionProvider"])
    width = session.get_inputs()[1].shape[1]
    actual = session.run(None, {"input_ids": ids, "image_features": np.zeros((0, width), dtype=np.float16)})[0][0].astype(np.float32)
    error = float(np.abs(actual - expected).max())
    if error > 1e-2:
        raise RuntimeError(f"The embedding model does not match the weights (max difference {error:.3f}).")


def is_adaface(weights: Path) -> bool:
    """A CVLFace IR-101 checkpoint (AdaFace, P22): its config.json names the network in "conf"."""
    config = json.loads((weights / "config.json").read_text(encoding="utf-8"))
    return config.get("conf", {}).get("name") == "ir101"


def convert_adaface(weights: Path, output: Path) -> int:
    """The face recognizer: one ONNX exported from PyTorch on the CPU, checked against PyTorch."""
    sys.path.insert(0, str(HERE / "recipes" / "adaface_ir101"))
    import export  # noqa: PLC0415

    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"{output} is not empty.")
    export.export(weights, output, progress)
    print("PF-DONE", flush=True)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--weights", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    weights, output, work = args.weights.resolve(), args.output.resolve(), args.work.resolve()
    try:
        if is_adaface(weights):
            return convert_adaface(weights, output)

        steps = len(PARTS) + 2
        progress(0, steps, "check")
        recipe_name = check_weights(weights)
        if output.exists() and any(output.iterdir()):
            raise FileExistsError(f"{output} is not empty.")

        # A working copy of the recipe pointed at the local weights.
        recipe = work / "recipe"
        if recipe.exists():
            # PhotoFlow empties the work folder before (only its own cleaner deletes files, decision B1).
            raise FileExistsError(f"{recipe} already exists: remove it first.")
        shutil.copytree(HERE / "recipes" / recipe_name, recipe, ignore=shutil.ignore_patterns("__pycache__"))
        os.environ["PF_QWEN_WEIGHTS"] = str(weights)
        for part in PARTS:
            path = recipe / "cuda" / f"{part}.json"
            config = json.loads(path.read_text(encoding="utf-8"))
            config["input_model"]["model_path"] = str(weights)
            path.write_text(json.dumps(config, indent=4), encoding="utf-8")

        os.chdir(recipe)
        sys.path.insert(0, str(recipe))
        prepare_onnxruntime()
        from olive import run  # noqa: PLC0415 - heavy import only when converting

        import optimize  # noqa: PLC0415 - the recipe's own GenAI config writer

        for step, part in enumerate(PARTS, start=1):
            progress(step, steps, part)
            run(str(recipe / "cuda" / f"{part}.json"))
            # Olive logs a failed pass and goes on: the model of the step must be there.
            if not (recipe / "cuda" / "models" / f"{part}.onnx").exists():
                raise RuntimeError(f"The {part} model was not produced (see the log above).")

        progress(len(PARTS) + 1, steps, "config")
        models = recipe / "cuda" / "models"
        check_embedding(models, weights)
        optimize.update_genai_config(output_dir=str(models), device="gpu")
        if hasattr(optimize, "fix_tokenizer"):
            optimize.fix_tokenizer(output_dir=str(models))  # Qwen3.5: regular expressions the GenAI tokenizer reads
        output.mkdir(parents=True, exist_ok=True)
        for item in models.iterdir():
            shutil.move(str(item), output / item.name)
        progress(steps, steps, "done")
        print("PF-DONE", flush=True)
        return 0
    except Exception as error:  # noqa: BLE001 - reported to PhotoFlow as one line
        traceback.print_exc()
        print(f"PF-ERROR {type(error).__name__}: {error}".replace("\n", " "), flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
