# VLM conversion (level D, phase 5b)

Converts the original weights of the level D model into the ONNX models of ONNX Runtime GenAI (int4 text decoder, fp16
vision encoder, embedding) for CUDA. PhotoFlow runs it from Settings › Models with the Python found on the PC, in a
private virtual environment (decisions U2, U3 in `docs/plans/fase-5b.md`). Two models (U1, revised on 15 Oct 2026):

- **Qwen3.5-9B** (Apache-2.0, <https://huggingface.co/Qwen/Qwen3.5-9B>): the model of new installations; natively
  multimodal (image-text-to-text).
- **Qwen2.5-VL-7B-Instruct** (Apache-2.0, <https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct>): the previous model, kept
  where it is already converted until the user chooses Qwen3.5.

Files:

- `convert.py` — entry point: `python convert.py --weights <folder> --output <folder> --work <folder>`; the recipe comes
  from the `model_type` of the weights; prints `PF-PROGRESS <step>/<steps> <name>` lines and `PF-DONE` / `PF-ERROR <message>`.
- `requirements.txt` — pinned packages (CUDA 13 wheels), the same for both models.
- `recipes/qwen3_5/` — Microsoft's Olive recipe for Qwen3.5 (MIT, `microsoft/olive-recipes@38e17a606cda`,
  `Qwen-Qwen3.5-9B/builtin`): `optimize.py`, `user_script.py`, `codes/`, `cuda/*.json`. Changes:
  - `user_script.py`: the model is read from `PF_QWEN_WEIGHTS` (the local folder) instead of the hub; a checkpoint
    weight that does not reach the model stops the export (the recipe only warned, and the weight stayed random).
  - `optimize.py`: only the configuration writers are kept; generation also ends at `<|im_end|>` (the recipe only had
    `<|endoftext|>`, and a chat answer would not stop); the image is at most about one megapixel (the recipe allowed 16).
  - `cuda/text.json`: `exclude_mtp` — the Model Builder of ONNX Runtime GenAI 0.17.1 also exports the multi-token
    prediction head of the checkpoint, which the recipe's `prune_lm_head` does not allow; a caption does not need it.
    `exclude_embeds` — without it the Model Builder 0.17.1 builds the text-only variant (`qwen3_5_text`), which the
    runtime cannot pair with the separate embedding and vision models (as for Qwen2.5-VL).
  - `cuda/embedding.json`: the table of 248k tokens is quantized to int8 by blocks of 128 after the fp16 pass (1.9 →
    1.0 GB): the peak GPU memory of the whole model goes from 11.7 to 9.8 GB, so 12 GB cards keep level D (P16);
    10 of 12 test captions identical to the fp16 table, the other two equivalent (15 Oct 2026).
  - `optimize.py` also sets a repetition penalty of 1.05: greedy Qwen3.5 repeated words or sentences in 4 of 12 test
    captions (17 repeated four-word groups); with the penalty none with the fp16 table, one group in each of 2 captions
    with the int8 table.
- `recipes/qwen2_5_vl/` — Microsoft's Olive recipe for Qwen2.5-VL (MIT, `microsoft/olive-recipes@232aa122155f`,
  `Qwen-Qwen2.5-VL-3B-Instruct/builtin`). Changes in `user_script.py`: the model is read from `PF_QWEN_WEIGHTS`; the size
  of the image features of the embedding export comes from the model (the recipe has the 3B value); the language
  weights, named `model.*` in the published checkpoint and `language_model.*` in the recipe's modeling code, are read
  under their checkpoint names (transformers would leave them at random with only a warning). In `cuda/text.json` the
  text model is built with `exclude_embeds` (ONNX Runtime GenAI 0.17 needs the separate embedding model).
- After the export `convert.py` checks that the embedding model gives the checkpoint's own vectors, and stops otherwise.
- `recipes/adaface_ir101/export.py` — the optional face recognizer AdaFace IR-101 WebFace12M (P22,
  <https://huggingface.co/minchul/cvlface_adaface_ir101_webface12m>, non-commercial): PhotoFlow's own IR-101 network
  (the CVLFace layout, weights loaded strictly from `model.safetensors`), exported on the CPU to `adaface_ir101.onnx`
  (opset 17, input `input` [N,3,112,112], output `embedding` [N,512]) and checked against PyTorch (cosine ≥ 0.9999).
  `convert.py` picks it when `config.json` names the network `ir101`; no GPU and no Olive needed. The parity with the
  authors' own network is in `tools/lab/adaface_refs.py`.
- `parity.py` — approximate parity (decision D5b-7, not shipped with the app): the converted model and the original
  PyTorch model caption the same photos, greedy (Qwen3.5 without thinking, as in PhotoFlow); the report counts identical
  captions, tokens in common and times.

The files are only read from the weights folder; the output folder is created new.
