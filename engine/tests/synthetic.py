"""Immagini di prova sintetiche con rumore noto."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import tifffile
from skimage import data

from denoiser.color import srgb_decode

# Matrice XYZ (D65) -> sRGB lineare: con questa ColorMatrix1 la "fotocamera" sintetica
# registra direttamente sRGB lineare, quindi lo sviluppo deve restituire l'immagine pulita.
_XYZ_TO_SRGB = [
    [3.2406, -1.5372, -0.4986],
    [-0.9689, 1.8758, 0.0415],
    [0.0557, -0.2040, 1.0570],
]


def clean_display(size: int | None = None) -> np.ndarray:
    img = (data.astronaut() / 255.0).astype(np.float32)
    return img[:size, :size] if size else img


def clean_linear(size: int | None = None, scale: float = 0.6) -> np.ndarray:
    return srgb_decode(clean_display(size)) * scale


def poisson_gaussian(linear: np.ndarray, a: float, b: float, seed: int = 0) -> np.ndarray:
    rng = np.random.default_rng(seed)
    noisy = rng.poisson(np.maximum(linear, 0) / a) * a + rng.normal(0, np.sqrt(b), linear.shape)
    return noisy.astype(np.float32)


def write_bayer_dng(
    path: Path,
    linear: np.ndarray,
    a: float = 0.0,
    b: float = 0.0,
    black: int = 256,
    white: int = 4095,
    seed: int = 0,
    wb: tuple[float, float, float] = (1.0, 1.0, 1.0),
) -> Path:
    """Scrive un DNG Bayer RGGB a partire da un'immagine lineare, con rumore Poisson-gaussiano opzionale.

    ``wb``: moltiplicatori del bilanciamento del bianco (verde = 1). I dati del sensore sono
    ``linear / wb``, come in una fotocamera vera dove R e B ricevono meno segnale del verde.
    """
    linear = np.asarray(linear, dtype=np.float32) / np.asarray(wb, dtype=np.float32)
    h, w = linear.shape[:2]
    h, w = h - h % 2, w - w % 2
    mosaic = np.empty((h, w), dtype=np.float32)
    mosaic[0::2, 0::2] = linear[0:h:2, 0:w:2, 0]
    mosaic[0::2, 1::2] = linear[0:h:2, 1:w:2, 1]
    mosaic[1::2, 0::2] = linear[1:h:2, 0:w:2, 1]
    mosaic[1::2, 1::2] = linear[1:h:2, 1:w:2, 2]
    if a > 0 or b > 0:
        mosaic = poisson_gaussian(mosaic, max(a, 1e-9), b, seed)
    dn = np.clip(np.round(mosaic * (white - black) + black), 0, white).astype(np.uint16)

    matrix: list[int] = []
    for v in np.asarray(_XYZ_TO_SRGB).ravel():
        matrix += [int(round(v * 10000)), 10000]
    tags = [
        (33421, "H", 2, (2, 2), True),  # CFARepeatPatternDim
        (33422, "B", 4, (0, 1, 1, 2), True),  # CFAPattern: R G / G B
        (50706, "B", 4, (1, 4, 0, 0), True),  # DNGVersion
        (50707, "B", 4, (1, 2, 0, 0), True),  # DNGBackwardVersion
        (50708, "s", 0, "Sintetica Bayer", True),  # UniqueCameraModel
        (50714, "I", 1, (black,), True),  # BlackLevel
        (50717, "I", 1, (white,), True),  # WhiteLevel
        (50721, "2i", 9, tuple(matrix), True),  # ColorMatrix1
        (50778, "H", 1, (21,), True),  # CalibrationIlluminant1 = D65
        (50728, "2I", 3, tuple(v for m in wb for v in (int(round(10000 / m)), 10000)), True),  # AsShotNeutral
        (271, "s", 0, "Sintetica", True),  # Make
        (272, "s", 0, "Sintetica Bayer", True),  # Model
    ]
    path = Path(path).with_suffix(".dng")
    tifffile.imwrite(path, dn, photometric=32803, subfiletype=0, extratags=tags, metadata=None)
    return path
