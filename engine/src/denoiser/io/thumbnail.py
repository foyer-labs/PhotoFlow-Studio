"""Miniature per il carosello: veloci, senza sviluppare il RAW quando la fotocamera ne ha già una."""

from __future__ import annotations

import io
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps

from .formats import kind_of


def _fit(im: Image.Image, size: int) -> np.ndarray:
    im = im.convert("RGB")
    im.thumbnail((size, size), Image.Resampling.LANCZOS)
    return np.ascontiguousarray(np.asarray(im, dtype=np.uint8))


def _orient_flip(arr: np.ndarray, flip: int) -> np.ndarray:
    from ..bayer import orient

    return np.ascontiguousarray(orient(arr, flip))


def _raw_thumbnail(path: Path, size: int) -> np.ndarray:
    import rawpy

    with rawpy.imread(str(path)) as raw:
        flip = int(raw.sizes.flip)
        try:
            thumb = raw.extract_thumb()
        except (rawpy.LibRawNoThumbnailError, rawpy.LibRawUnsupportedThumbnailError):
            thumb = None
        if thumb is not None and thumb.format == rawpy.ThumbFormat.JPEG:
            with Image.open(io.BytesIO(thumb.data)) as im:
                im.draft("RGB", (size * 2, size * 2))
                # L'anteprima incorporata di solito non è ruotata: se non ha un proprio orientamento
                # si usa quello del RAW.
                oriented = im.getexif().get(0x0112, 1) not in (None, 1)
                arr = _fit(ImageOps.exif_transpose(im), size)
            return arr if oriented else _orient_flip(arr, flip)
        if thumb is not None and thumb.format == rawpy.ThumbFormat.BITMAP:
            return _orient_flip(_fit(Image.fromarray(np.asarray(thumb.data, dtype=np.uint8)), size), flip)
        # Nessuna anteprima (es. DNG senza miniatura): sviluppo rapido a metà risoluzione, già orientato.
        rgb = raw.postprocess(half_size=True, use_camera_wb=True, output_bps=8, no_auto_bright=False)
    return _fit(Image.fromarray(rgb), size)


def _raster_thumbnail(path: Path, size: int) -> np.ndarray:
    try:
        with Image.open(path) as im:
            im.draft("RGB", (size * 2, size * 2))  # i JPEG si decodificano già ridotti
            return _fit(ImageOps.exif_transpose(im), size)
    except (OSError, ValueError):
        # Es. TIFF a 16 bit per canale, che Pillow non converte: lettura completa.
        from .loader import load_image

        image = load_image(path)
        rgb = np.clip(image.pixels * 255.0 + 0.5, 0, 255).astype(np.uint8)
        return _fit(Image.fromarray(rgb), size)


def make_thumbnail(path: str | Path, size: int = 160) -> np.ndarray:
    """Miniatura RGB uint8 (lato lungo ``size``) nell'orientamento giusto."""
    path = Path(path)
    if kind_of(path) == "raw":
        return _raw_thumbnail(path, size)
    return _raster_thumbnail(path, size)
