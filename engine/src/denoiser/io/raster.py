"""Lettura di JPG, PNG e TIFF (8 o 16 bit)."""

from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageOps

from ..image import Domain, ImageData, ImageMeta
from .exif import read_basic_exif


def _normalize(arr: np.ndarray) -> tuple[np.ndarray, int]:
    if arr.dtype == np.uint8:
        return arr.astype(np.float32) / 255.0, 8
    if arr.dtype == np.uint16:
        return arr.astype(np.float32) / 65535.0, 16
    if np.issubdtype(arr.dtype, np.floating):
        return np.clip(arr.astype(np.float32), 0.0, 1.0), 32
    raise ValueError(f"Tipo di pixel non supportato: {arr.dtype}")


def _split_channels(arr: np.ndarray) -> tuple[np.ndarray, np.ndarray | None]:
    """Restituisce (RGB, alfa) a partire da immagini in scala di grigi, RGB o RGBA."""
    if arr.ndim == 2:
        return np.repeat(arr[..., None], 3, axis=2), None
    if arr.shape[2] == 1:
        return np.repeat(arr, 3, axis=2), None
    if arr.shape[2] == 2:  # grigio + alfa
        return np.repeat(arr[..., :1], 3, axis=2), arr[..., 1]
    if arr.shape[2] == 4:
        return arr[..., :3], arr[..., 3]
    return arr[..., :3], None


def _apply_orientation(arr: np.ndarray, orientation: int) -> np.ndarray:
    """Ruota/rifletti come indicato dal tag EXIF Orientation (1-8)."""
    ops = {
        2: lambda a: a[:, ::-1],
        3: lambda a: a[::-1, ::-1],
        4: lambda a: a[::-1],
        5: lambda a: np.swapaxes(a, 0, 1),
        6: lambda a: np.swapaxes(a, 0, 1)[:, ::-1],
        7: lambda a: np.swapaxes(a, 0, 1)[::-1, ::-1],
        8: lambda a: np.swapaxes(a, 0, 1)[::-1],
    }
    op = ops.get(orientation)
    return np.ascontiguousarray(op(arr)) if op else arr


def load_jpeg(path: Path) -> ImageData:
    with Image.open(path) as im:
        exif_bytes = im.info.get("exif")
        icc = im.info.get("icc_profile")
        if im.mode == "CMYK":
            icc = None  # dopo la conversione in RGB il profilo CMYK non vale più
        im = ImageOps.exif_transpose(im)
        rgb = np.asarray(im.convert("RGB"))
    pixels, _ = _normalize(rgb)
    info = read_basic_exif(path)
    meta = ImageMeta(
        source=path, kind="jpeg", camera=info.camera, make=info.make, model=info.model, iso=info.iso, date=info.date,
        bits_per_sample=8, exif_bytes=exif_bytes, icc_profile=icc or None,
    )
    return ImageData(pixels=pixels, domain=Domain.DISPLAY, meta=meta)


def _pil_info(path: Path) -> tuple[str, bytes | None, int]:
    """(modo, profilo ICC, orientamento EXIF) letti con Pillow senza decodificare i pixel."""
    try:
        with Image.open(path) as im:
            orientation = int(im.getexif().get(0x0112, 1) or 1)
            return im.mode, im.info.get("icc_profile") or None, orientation
    except Exception:
        return "", None, 1


def load_png_or_tiff(path: Path, kind: str) -> ImageData:
    mode, icc, orientation = _pil_info(path)
    if mode == "CMYK":
        # cv2 aggiungerebbe un canale alfa finto: si converte con Pillow (senza il profilo CMYK).
        with Image.open(path) as im:
            arr = np.asarray(im.convert("RGB"))
        icc = None
    else:
        # cv2.imdecode su un buffer evita i problemi di cv2.imread con percorsi non ASCII su Windows.
        buf = np.fromfile(str(path), dtype=np.uint8)
        arr = cv2.imdecode(buf, cv2.IMREAD_UNCHANGED)
        if arr is None:
            raise ValueError(f"Impossibile leggere {path.name}")
        if arr.ndim == 3 and arr.shape[2] >= 3:
            order = [2, 1, 0] + ([3] if arr.shape[2] == 4 else [])
            arr = arr[..., order]
    # cv2 con IMREAD_UNCHANGED ignora l'orientamento EXIF: lo si applica qui (nell'uscita viene azzerato).
    arr = _apply_orientation(arr, orientation)
    norm, bits = _normalize(arr)
    rgb, alpha = _split_channels(norm)
    info = read_basic_exif(path)
    meta = ImageMeta(
        source=path, kind=kind, camera=info.camera, make=info.make, model=info.model, iso=info.iso, date=info.date,
        bits_per_sample=bits, icc_profile=icc,
    )
    return ImageData(pixels=np.ascontiguousarray(rgb), domain=Domain.DISPLAY, meta=meta, alpha=alpha)
