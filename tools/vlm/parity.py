# SPDX-License-Identifier: Apache-2.0
"""Approximate parity of the level D model (phase 5b, decision D5b-7).

The converted ONNX Runtime GenAI model (int4) and the original PyTorch model (bf16, transformers) caption the same
photos with the same prompt, greedy, at most 128 new tokens. Each photo is first reduced as PhotoFlow does (longest
side 1024 px, JPEG quality 92) into the work folder, so both runtimes read the same file. The int4 weights change the
model, so there is no exact threshold: the report counts identical captions, the tokens in common and the times, and
the captions go side by side for a look.

    python tools/vlm/parity.py --weights <original folder> --model <converted folder> --photos <folder> --work <folder>
        [--count 10] [--out report.json]

Needs the environment of requirements.txt plus Pillow and torchvision (`pip install pillow torchvision`, same CUDA index) and an NVIDIA GPU with about 18 GB (Qwen2.5-VL-7B) or 21 GB (Qwen3.5-9B) for the PyTorch
model; the two models run one after the other. The model (Qwen3.5 or Qwen2.5-VL) comes from the weights; Qwen3.5 answers
without thinking, as in PhotoFlow. The photos are only read.
"""

from __future__ import annotations

import argparse
import difflib
import gc
import json
import sys
import time
from pathlib import Path

PROMPT = "Descrivi questa foto in italiano in una o due frasi, in modo oggettivo, senza inventare nomi di persone o luoghi."
MAX_TOKENS = 128
MAX_SIDE = 1024


def prepare(photos: list[Path], work: Path) -> list[Path]:
    from PIL import Image, ImageOps  # noqa: PLC0415

    work.mkdir(parents=True, exist_ok=True)
    prepared = []
    for photo in photos:
        with Image.open(photo) as image:
            image = ImageOps.exif_transpose(image).convert("RGB")
            scale = min(1.0, MAX_SIDE / max(image.size))
            if scale < 1:
                image = image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))), Image.BILINEAR)
            target = work / (photo.stem + ".jpg")
            image.save(target, quality=92)
            prepared.append(target)
    return prepared


def gpu_runtime() -> None:
    """The CUDA libraries of the PyTorch wheel for both runtimes (as in convert.py): another cuDNN first on the path breaks torch."""
    import onnxruntime as ort  # noqa: PLC0415
    import torch  # noqa: F401, PLC0415

    ort.preload_dlls()


def is_qwen35(weights: Path) -> bool:
    return json.loads((weights / "config.json").read_text(encoding="utf-8")).get("model_type") == "qwen3_5"


def chat(qwen35: bool) -> str:
    """The chat PhotoFlow.Vlm sends (Program.cs): Qwen3.5 with an empty think block, Qwen2.5-VL with its system line."""
    if qwen35:
        return ("<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>" + PROMPT + "<|im_end|>\n"
                "<|im_start|>assistant\n<think>\n\n</think>\n\n")
    return ("<|im_start|>system\nYou are a helpful assistant.<|im_end|>\n<|im_start|>user\n"
            "<|vision_start|><|image_pad|><|vision_end|>" + PROMPT + "<|im_end|>\n<|im_start|>assistant\n")


def run_genai(model_dir: Path, images: list[Path], qwen35: bool) -> list[dict]:
    import onnxruntime_genai as og  # noqa: PLC0415

    model = og.Model(str(model_dir))
    processor = model.create_multimodal_processor()
    tokenizer = og.Tokenizer(model)
    chat_text = chat(qwen35)
    results = []
    for image in images:
        start = time.perf_counter()
        inputs = processor(chat_text, images=og.Images.open(str(image)))
        params = og.GeneratorParams(model)
        params.set_search_options(do_sample=False, max_length=2048)  # as PhotoFlow.Vlm (its KV cache, its memory)
        generator = og.Generator(model, params)
        generator.set_inputs(inputs)
        tokens = []
        while len(tokens) < MAX_TOKENS:
            generator.generate_next_token()
            if generator.is_done():
                break
            tokens.append(int(generator.get_next_tokens()[0]))
        del generator
        results.append({"tokens": tokens, "text": tokenizer.decode(tokens).strip(), "ms": (time.perf_counter() - start) * 1000})
    del processor, tokenizer, model
    gc.collect()
    return results


def repetition_penalty(model_dir: Path) -> float:
    """The repetition penalty the converted model generates with (genai_config.json), applied to PyTorch too."""
    config = json.loads((model_dir / "genai_config.json").read_text(encoding="utf-8"))
    return float(config.get("search", {}).get("repetition_penalty", 1.0))


def run_torch(weights: Path, images: list[Path], qwen35: bool, penalty: float = 1.0) -> list[dict]:
    import torch  # noqa: PLC0415
    from transformers import AutoModelForImageTextToText, AutoProcessor  # noqa: PLC0415

    model = AutoModelForImageTextToText.from_pretrained(str(weights), dtype=torch.bfloat16).to("cuda").eval()
    processor = AutoProcessor.from_pretrained(str(weights))
    tokenizer = processor.tokenizer
    stop = {t for t in (tokenizer.eos_token_id, tokenizer.pad_token_id, tokenizer.convert_tokens_to_ids("<|endoftext|>")) if t is not None}
    results = []
    for image in images:
        from PIL import Image  # noqa: PLC0415

        start = time.perf_counter()
        messages = [{"role": "user", "content": [{"type": "image"}, {"type": "text", "text": PROMPT}]}]
        extra = {"enable_thinking": False} if qwen35 else {}
        text = processor.apply_chat_template(messages, add_generation_prompt=True, tokenize=False, **extra)
        with Image.open(image) as picture:
            inputs = processor(text=[text], images=[picture.convert("RGB")], return_tensors="pt").to("cuda")
        with torch.inference_mode():
            output = model.generate(**inputs, max_new_tokens=MAX_TOKENS, do_sample=False, repetition_penalty=penalty)
        tokens = [int(t) for t in output[0, inputs["input_ids"].shape[1]:] if int(t) not in stop]
        results.append({"tokens": tokens, "text": processor.decode(tokens, skip_special_tokens=True).strip(),
                        "ms": (time.perf_counter() - start) * 1000})
    del model
    torch.cuda.empty_cache()
    return results


def compare(onnx: dict, reference: dict) -> dict:
    a, b = onnx["tokens"], reference["tokens"]
    prefix = next((i for i, (x, y) in enumerate(zip(a, b)) if x != y), min(len(a), len(b)))
    matcher = difflib.SequenceMatcher(a=a, b=b, autojunk=False)
    common = sum(block.size for block in matcher.get_matching_blocks())
    return {
        "identical": onnx["text"] == reference["text"],
        "common_prefix_tokens": prefix,
        "common_tokens_ratio": round(2 * common / max(1, len(a) + len(b)), 3),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--weights", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--count", type=int, default=10)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8")

    found = sorted(p for p in args.photos.iterdir() if p.suffix.lower() in (".jpg", ".jpeg", ".png"))
    step = max(1, len(found) // args.count)
    photos = found[::step][: args.count]
    images = prepare(photos, args.work)
    gpu_runtime()
    qwen35 = is_qwen35(args.weights)
    onnx = run_genai(args.model, images, qwen35)
    reference = run_torch(args.weights, images, qwen35, repetition_penalty(args.model))

    rows = []
    for photo, o, r in zip(photos, onnx, reference):
        rows.append({"photo": photo.name, "onnx": o["text"], "pytorch": r["text"], "onnx_ms": round(o["ms"]), "pytorch_ms": round(r["ms"]),
                     "onnx_tokens": len(o["tokens"]), "pytorch_tokens": len(r["tokens"]), **compare(o, r)})
    summary = {
        "photos": len(rows),
        "identical": sum(r["identical"] for r in rows),
        "mean_common_tokens_ratio": round(sum(r["common_tokens_ratio"] for r in rows) / max(1, len(rows)), 3),
        "mean_common_prefix_tokens": round(sum(r["common_prefix_tokens"] for r in rows) / max(1, len(rows)), 1),
        # The first photo pays the warm-up of each runtime.
        "onnx_ms_median": sorted(r["onnx_ms"] for r in rows)[len(rows) // 2],
        "pytorch_ms_median": sorted(r["pytorch_ms"] for r in rows)[len(rows) // 2],
    }
    report = {"prompt": PROMPT, "max_tokens": MAX_TOKENS, "summary": summary, "rows": rows}
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
