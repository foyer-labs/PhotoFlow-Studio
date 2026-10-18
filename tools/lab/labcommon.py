# SPDX-License-Identifier: Apache-2.0
"""Shared helpers for the PhotoFlow lab scripts.

Every output goes to PHOTOFLOW_TESTDATA (outside the repository): the helpers refuse
any output folder inside the repository so that models, references and reports can
never be committed by mistake (CLAUDE.md §6, §8).
"""

from __future__ import annotations

import hashlib
import json
import os
import platform
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "models" / "manifest.json"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp"}
RAW_EXTENSIONS = {".nef", ".nrw", ".cr2", ".cr3", ".arw", ".raf", ".dng", ".orf", ".rw2", ".pef", ".srw"}


def testdata_root() -> Path:
    value = os.environ.get("PHOTOFLOW_TESTDATA")
    if not value:
        sys.exit("PHOTOFLOW_TESTDATA is not set: lab outputs must live outside the repository.")
    root = Path(value).resolve()
    root.mkdir(parents=True, exist_ok=True)
    return root


def output_dir(*parts: str) -> Path:
    """A folder under PHOTOFLOW_TESTDATA, created if needed; never inside the repository."""
    path = testdata_root().joinpath(*parts).resolve()
    ensure_outside_repo(path)
    path.mkdir(parents=True, exist_ok=True)
    return path


def ensure_outside_repo(path: Path) -> None:
    resolved = path.resolve()
    if resolved == REPO_ROOT or REPO_ROOT in resolved.parents:
        sys.exit(f"Refusing to write inside the repository: {resolved}")


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


@dataclass(frozen=True)
class ManifestFile:
    model_id: str
    path: str
    url: str
    sha256: str
    size: int
    archive: str | None


def load_manifest() -> dict:
    with MANIFEST_PATH.open(encoding="utf-8") as stream:
        return json.load(stream)


def manifest_model(model_id: str) -> dict:
    for model in load_manifest()["models"]:
        if model["id"] == model_id:
            return model
    sys.exit(f"Model '{model_id}' not found in {MANIFEST_PATH}")


def manifest_files(model: dict) -> list[ManifestFile]:
    return [
        ManifestFile(model["id"], f["path"], f["url"], f["sha256"], f["size"], f.get("archive"))
        for f in model["files"]
    ]


def model_dir(model_id: str) -> Path:
    return output_dir("models", model_id)


def model_file(model_id: str, path: str) -> Path:
    """Local path of a verified manifest file (run fetch_models.py first)."""
    local = model_dir(model_id) / path
    if not local.exists():
        sys.exit(f"{local} missing: run 'uv run python fetch_models.py {model_id}' first.")
    return local


def download(url: str, target: Path, expected_sha256: str, expected_size: int) -> None:
    """Download to a temporary name, verify size and SHA-256, then rename (never keeps a bad file)."""
    ensure_outside_repo(target)
    if target.exists() and target.stat().st_size == expected_size and sha256_of(target) == expected_sha256:
        print(f"  ok (cached)  {target.name}")
        return
    partial = target.with_name(target.name + ".part")
    request = urllib.request.Request(url, headers={"User-Agent": "photoflow-lab"})
    with urllib.request.urlopen(request) as response, partial.open("wb") as out:
        received = 0
        while chunk := response.read(1 << 20):
            out.write(chunk)
            received += len(chunk)
            print(f"\r  {target.name}: {received / 1e6:8.1f} / {expected_size / 1e6:.1f} MB", end="", flush=True)
    print()
    actual_size = partial.stat().st_size
    actual_sha = sha256_of(partial)
    if actual_size != expected_size or actual_sha != expected_sha256:
        partial.replace(partial.with_suffix(".rejected"))
        sys.exit(f"Verification failed for {url}: size {actual_size} (expected {expected_size}), sha256 {actual_sha}")
    partial.replace(target)
    print(f"  ok (verified) {target.name}")


EXIFTOOL_VERSION = "13.59"
EXIFTOOL_URL = "https://sourceforge.net/projects/exiftool/files/exiftool-13.59_64.zip/download"
EXIFTOOL_SHA256 = "44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec"  # https://exiftool.org/checksums.txt
EXIFTOOL_SIZE = 11183675


def ensure_exiftool() -> Path:
    """exiftool for the lab, downloaded once into PHOTOFLOW_TESTDATA/tools (Windows build, SHA-256 verified)."""
    import shutil
    import zipfile

    folder = output_dir("tools", f"exiftool-{EXIFTOOL_VERSION}")
    exe = folder / "exiftool.exe"
    if exe.exists():
        return exe
    if platform.system() != "Windows":
        found = shutil.which("exiftool")
        if found:
            return Path(found)
        sys.exit("exiftool not found: install it with your package manager.")
    archive = folder / "exiftool.zip"
    download(EXIFTOOL_URL, archive, EXIFTOOL_SHA256, EXIFTOOL_SIZE)
    with zipfile.ZipFile(archive) as zf:
        zf.extractall(folder)
    inner = next(folder.glob("exiftool-*_64"))
    for item in inner.iterdir():
        item.replace(folder / item.name)
    (folder / "exiftool(-k).exe").replace(exe)
    return exe


def list_images(folder: Path, include_raw: bool = False) -> list[Path]:
    allowed = IMAGE_EXTENSIONS | (RAW_EXTENSIONS if include_raw else set())
    return sorted(p for p in folder.rglob("*") if p.is_file() and p.suffix.lower() in allowed)


def environment_info() -> dict:
    info = {"python": platform.python_version(), "platform": platform.platform(), "processor": platform.processor()}
    try:
        import torch

        info["torch"] = torch.__version__
        if torch.cuda.is_available():
            props = torch.cuda.get_device_properties(0)
            info["gpu"] = props.name
            info["gpu_memory_gb"] = round(props.total_memory / 2**30, 1)
    except ImportError:
        pass
    try:
        import onnxruntime

        info["onnxruntime"] = onnxruntime.__version__
        info["ort_providers"] = onnxruntime.get_available_providers()
    except ImportError:
        pass
    info["logical_cpus"] = os.cpu_count()
    return info


def load_photo(path: Path):
    """Photo as upright RGB Pillow image: the largest embedded JPEG for RAW files (orientation from the RAW)."""
    import io
    import subprocess

    from PIL import Image, ImageOps

    if path.suffix.lower() in RAW_EXTENSIONS:
        exe = str(ensure_exiftool())
        data = b""
        for tag in ("JpgFromRaw", "PreviewImage"):
            data = subprocess.run([exe, "-b", f"-{tag}", str(path)], capture_output=True, check=True).stdout
            if len(data) > 1000:
                break
        image = Image.open(io.BytesIO(data))
        orientation = subprocess.run([exe, "-n", "-s3", "-Orientation", str(path)], capture_output=True, text=True).stdout.strip()
        image = image.convert("RGB")
        transpose = {"2": Image.Transpose.FLIP_LEFT_RIGHT, "3": Image.Transpose.ROTATE_180, "4": Image.Transpose.FLIP_TOP_BOTTOM,
                     "5": Image.Transpose.TRANSPOSE, "6": Image.Transpose.ROTATE_270, "7": Image.Transpose.TRANSVERSE,
                     "8": Image.Transpose.ROTATE_90}.get(orientation)
        return image.transpose(transpose) if transpose else image
    return ImageOps.exif_transpose(Image.open(path)).convert("RGB")
