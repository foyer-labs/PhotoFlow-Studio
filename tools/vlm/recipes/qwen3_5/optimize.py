# Microsoft Olive recipe for Qwen3.5 (MIT, microsoft/olive-recipes@38e17a606cda, Qwen-Qwen3.5-9B/builtin), adapted for
# PhotoFlow: only the configuration writers are kept (convert.py runs the Olive passes); generation ends at <|im_end|>
# as well as <|endoftext|> (the recipe only had the latter, and a chat answer would not stop); the image is at most
# about one megapixel (the recipe allowed 16), which bounds the image tokens and the memory.
"""GenAI runtime configuration of the exported Qwen3.5 models."""
import json
from pathlib import Path

MODELS_DIR = "models"

# Qwen3.5 tokenizer: <|endoftext|>, <|im_end|>, image and vision tokens.
END_OF_TEXT, IM_END = 248044, 248046
MAX_PIXELS = 1024 * 1024
REPETITION_PENALTY = 1.05


def update_genai_config(output_dir: str = MODELS_DIR, device: str = "cpu"):
    """Patch genai_config.json with embedding/vision sections and processor_config."""
    config_path = Path(output_dir) / "genai_config.json"

    with open(config_path) as f:
        config = json.load(f)

    if device == "gpu":
        provider_options = [
            {"cuda": {"enable_cuda_graph": "1", "enable_skip_layer_norm_strict_mode": "1"}}
        ]
        # Vision model has Loop nodes (one per ViT block) which are incompatible
        # with CUDA graph capture, so disable it for vision and embedding only.
        vision_provider_options = [
            {"cuda": {"enable_cuda_graph": "0", "enable_skip_layer_norm_strict_mode": "1"}}
        ]
    else:
        provider_options = []
        vision_provider_options = []

    session_options = {"log_id": "onnxruntime-genai", "provider_options": provider_options}
    vision_session_options = {"log_id": "onnxruntime-genai", "provider_options": vision_provider_options}

    config["model"]["decoder"]["session_options"] = session_options

    config["model"]["embedding"] = {
        "filename": "embedding.onnx",
        "inputs": {"input_ids": "input_ids", "image_features": "image_features"},
        "outputs": {"inputs_embeds": "inputs_embeds"},
        "session_options": vision_session_options,
    }

    config["model"]["vision"] = {
        "filename": "vision.onnx",
        "config_filename": "processor_config.json",
        "spatial_merge_size": 2,
        "tokens_per_second": 2.0,
        "patch_size": 16,
        "inputs": {"pixel_values": "pixel_values", "image_grid_thw": "image_grid_thw"},
        "outputs": {"image_features": "image_features"},
        "session_options": vision_session_options,
    }

    config["model"]["bos_token_id"] = END_OF_TEXT
    config["model"]["eos_token_id"] = [IM_END, END_OF_TEXT]
    config["model"]["pad_token_id"] = END_OF_TEXT
    config["model"]["image_token_id"] = 248056
    config["model"]["video_token_id"] = 248057
    config["model"]["vision_start_token_id"] = 248053

    config["search"]["top_k"] = 1
    if config["search"].get("top_p") is None:
        config["search"]["top_p"] = 1.0
    # Greedy Qwen3.5 repeats sentences in a caption; 1.05 removed every repetition on 12 test photos (15 Oct 2026).
    config["search"]["repetition_penalty"] = REPETITION_PENALTY

    with open(config_path, "w") as f:
        json.dump(config, f, indent=4)
    print(f"  Updated {config_path}")

    processor_config = {
        "processor": {
            "name": "qwen2_5_image_processor",
            "transforms": [
                {"operation": {"name": "decode_image", "type": "DecodeImage", "attrs": {"color_space": "RGB"}}},
                {"operation": {"name": "convert_to_rgb", "type": "ConvertRGB"}},
                {"operation": {"name": "resize", "type": "Resize", "attrs": {
                    "width": 960, "height": 672, "smart_resize": 1,
                    "min_pixels": 65536, "max_pixels": MAX_PIXELS, "patch_size": 16, "merge_size": 2,
                }}},
                {"operation": {"name": "rescale", "type": "Rescale", "attrs": {
                    "rescale_factor": 0.00392156862745098,
                }}},
                {"operation": {"name": "normalize", "type": "Normalize", "attrs": {
                    "mean": [0.5, 0.5, 0.5], "std": [0.5, 0.5, 0.5], "qwen2_5_vl": 1,
                }}},
                {"operation": {"name": "patch_image", "type": "PatchImage", "attrs": {
                    "patch_size": 16, "temporal_patch_size": 2, "merge_size": 2,
                }}},
            ],
        }
    }

    processor_path = Path(output_dir) / "processor_config.json"
    with open(processor_path, "w") as f:
        json.dump(processor_config, f, indent=2)
    print(f"  Created {processor_path}")


def fix_tokenizer(output_dir: str = MODELS_DIR):
    """Fix tokenizer.json for C++ std::regex compatibility.

    Qwen3.5's tokenizer uses Unicode property escapes (\\p{L}, \\p{N}) in its
    Split pre-tokenizer, which aren't supported by std::regex in onnxruntime-genai.
    Remove the Split and keep only ByteLevel with use_regex=True.
    """
    tk_path = Path(output_dir) / "tokenizer.json"
    if not tk_path.exists():
        return
    tk = json.loads(tk_path.read_text(encoding="utf-8"))
    pt = tk.get("pre_tokenizer", {})
    if pt.get("type") == "Sequence":
        pt["pretokenizers"] = [s for s in pt["pretokenizers"] if s.get("type") == "ByteLevel"]
        for s in pt["pretokenizers"]:
            s["use_regex"] = True
    tk_path.write_text(json.dumps(tk, ensure_ascii=False), encoding="utf-8")

    tc_path = Path(output_dir) / "tokenizer_config.json"
    if tc_path.exists():
        tc = json.loads(tc_path.read_text(encoding="utf-8"))
        tc["tokenizer_class"] = "Qwen2Tokenizer"
        tc_path.write_text(json.dumps(tc, indent=2, ensure_ascii=False), encoding="utf-8")
    print("  Fixed tokenizer for C++ std::regex compatibility")
