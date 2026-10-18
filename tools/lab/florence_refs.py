# SPDX-License-Identifier: Apache-2.0
"""Parity references for the Florence-2 caption adapter in C# (spec §8, level C, decision F5-1).

Reference: the original PyTorch model in transformers (native Florence-2, transformers >= 4.56, run in an overlay
environment because the lab pins 4.55 for D-FINE and RAM++), `florence-community/Florence-2-base-ft` pinned by commit,
fp32 on the CPU. Task "<DETAILED_CAPTION>", beam search 3, no repeated trigram, as the model's generation config.

Per image (stem): <stem>.rgb (input, RGB bytes, long side 1024), <stem>.pixels.bin (float32 [1,3,768,768]);
index.json: prompt token ids, and per image the generated token ids and the caption.

Usage:
    uv run --with "transformers==4.57.1" python florence_refs.py --photos E:/path/outside/repo/photos [--limit 20]
Output: PHOTOFLOW_TESTDATA/parity/florence/
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import torch
from PIL import Image

import labcommon as lc

MODEL = "florence-community/Florence-2-base-ft"
REVISION = "0b03b6f15a4a211370fb204aee4e7dd48887ea37"
TASK = "<DETAILED_CAPTION>"
INPUT_LONG_SIDE = 1024
MAX_NEW_TOKENS = 256


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--photos", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=20)
    args = parser.parse_args()
    photos = args.photos.resolve()
    lc.ensure_outside_repo(photos)
    out = lc.output_dir("parity", "florence")

    from transformers import AutoProcessor, Florence2ForConditionalGeneration

    cache = str(lc.testdata_root() / "hf-cache")
    processor = AutoProcessor.from_pretrained(MODEL, revision=REVISION, cache_dir=cache)
    model = Florence2ForConditionalGeneration.from_pretrained(MODEL, revision=REVISION, cache_dir=cache, torch_dtype=torch.float32).eval()

    index = {"model": MODEL, "revision": REVISION, "task": TASK, "images": []}
    for path in lc.list_images(photos)[: args.limit]:
        image = lc.load_photo(path)
        scale = INPUT_LONG_SIDE / max(image.size)
        if scale < 1:
            image = image.resize((round(image.width * scale), round(image.height * scale)), Image.Resampling.BILINEAR)
        (out / f"{path.stem}.rgb").write_bytes(np.asarray(image, dtype=np.uint8).tobytes())
        inputs = processor(text=TASK, images=image, return_tensors="pt")
        inputs["pixel_values"].numpy().astype(np.float32).tofile(out / f"{path.stem}.pixels.bin")
        index["prompt_ids"] = inputs["input_ids"][0].tolist()
        with torch.no_grad():
            generated = model.generate(input_ids=inputs["input_ids"], pixel_values=inputs["pixel_values"], max_new_tokens=MAX_NEW_TOKENS,
                                       num_beams=3, no_repeat_ngram_size=3, early_stopping=True, do_sample=False)
        ids = generated[0].tolist()
        raw = processor.batch_decode(generated, skip_special_tokens=False)[0]
        caption = processor.post_process_generation(raw, task=TASK, image_size=image.size)[TASK]
        index["images"].append({"stem": path.stem, "width": image.width, "height": image.height, "ids": ids, "caption": caption})
        print(f"{path.name}: {caption}")
    (out / "index.json").write_text(json.dumps(index, indent=1, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
