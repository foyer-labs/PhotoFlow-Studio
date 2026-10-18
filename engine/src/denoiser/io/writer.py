"""Scrittura delle immagini elaborate (TIFF, PNG, JPEG)."""

from __future__ import annotations

import struct
import zlib
from functools import lru_cache
from pathlib import Path

import cv2
import numpy as np
import tifffile
from PIL import Image


def _quantize(x: np.ndarray, bits: int) -> np.ndarray:
    maxv = 255 if bits == 8 else 65535
    dtype = np.uint8 if bits == 8 else np.uint16
    return np.round(np.clip(x, 0.0, 1.0) * maxv).astype(dtype)


@lru_cache(maxsize=1)
def srgb_profile() -> bytes:
    """Profilo ICC sRGB: dichiara lo spazio colore delle uscite da RAW (sviluppate in sRGB)."""
    from PIL import ImageCms

    return ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()


def _png_with_icc(encoded: bytes, icc: bytes) -> bytes:
    """Inserisce il chunk iCCP (profilo colore) subito dopo IHDR."""
    body = b"ICC Profile\x00\x00" + zlib.compress(icc)
    chunk = struct.pack(">I", len(body)) + b"iCCP" + body + struct.pack(">I", zlib.crc32(b"iCCP" + body) & 0xFFFFFFFF)
    ihdr_end = 8 + 4 + 4 + 13 + 4  # firma + lunghezza + tipo + dati + CRC di IHDR
    return encoded[:ihdr_end] + chunk + encoded[ihdr_end:]


def save_image(
    pixels: np.ndarray,
    path: str | Path,
    fmt: str,
    bit_depth: int = 16,
    jpeg_quality: int = 95,
    alpha: np.ndarray | None = None,
    exif_bytes: bytes | None = None,
    description: str | None = None,
    icc_profile: bytes | None = None,
) -> Path:
    """Salva un'immagine (H, W, 3) float in [0, 1].

    ``fmt`` è "tiff", "png" o "jpeg"; il JPEG è sempre a 8 bit e senza alfa. ``icc_profile``: profilo
    colore da incorporare (quello dell'originale); se manca si dichiara sRGB.
    """
    icc = icc_profile or srgb_profile()
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if fmt == "jpeg":
        rgb8 = _quantize(pixels, 8)
        kwargs = dict(quality=int(jpeg_quality), subsampling=0 if jpeg_quality >= 90 else 2, optimize=True,
                      icc_profile=icc)
        if exif_bytes:
            kwargs["exif"] = _reset_orientation(exif_bytes)
        if description:
            kwargs["comment"] = description.encode("utf-8")
        Image.fromarray(rgb8, "RGB").save(path, "JPEG", **kwargs)
        return path

    bits = 8 if bit_depth == 8 else 16
    data = _quantize(pixels, bits)
    if alpha is not None:
        data = np.concatenate([data, _quantize(alpha, bits)[..., None]], axis=2)

    if fmt == "tiff":
        tifffile.imwrite(
            path,
            data,
            photometric="rgb",
            compression="zlib",
            extrasamples=["unassalpha"] if alpha is not None else None,
            description=description,
            software="Denoiser",
            iccprofile=icc,
            metadata=None,  # altrimenti tifffile aggiunge un secondo ImageDescription con la forma dell'array
        )
    elif fmt == "png":
        order = [2, 1, 0, 3] if alpha is not None else [2, 1, 0]
        ok, encoded = cv2.imencode(".png", np.ascontiguousarray(data[..., order]))
        if not ok:
            raise OSError(f"Impossibile codificare il PNG {path.name}")
        path.write_bytes(_png_with_icc(encoded.tobytes(), icc))
    else:
        raise ValueError(f"Formato di uscita sconosciuto: {fmt}")
    return path


def _reset_orientation(exif_bytes: bytes) -> bytes:
    """L'immagine è già stata ruotata in lettura: l'orientamento EXIF va riportato a "normale"."""
    try:
        exif = Image.Exif()
        exif.load(exif_bytes)
        exif[0x0112] = 1
        return exif.tobytes()
    except Exception:
        return exif_bytes
