"""Scrittura di file DNG, il formato RAW aperto di Adobe.

- **DNG Bayer**: i dati del sensore già ripuliti (livello 3). Il programma di fotoritocco
  esegue la propria demosaicizzazione e conserva tutta la libertà di un RAW (bilanciamento
  del bianco, esposizione, profili colore).
- **DNG lineare**: immagine RGB lineare già demosaicizzata (livello 2 o sensori non Bayer),
  con bilanciamento del bianco già applicato.
"""

from __future__ import annotations

import io
import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import tifffile

# Codici dei tag DNG/TIFF usati.
_CFA_REPEAT_DIM, _CFA_PATTERN = 33421, 33422
_DNG_VERSION, _DNG_BACKWARD = 50706, 50707
_UNIQUE_MODEL = 50708
_BLACK_REPEAT_DIM, _BLACK_LEVEL, _WHITE_LEVEL = 50713, 50714, 50717
_COLOR_MATRIX1, _ILLUMINANT1 = 50721, 50778
_AS_SHOT_NEUTRAL, _BASELINE_EXPOSURE = 50728, 50730
_DEFAULT_CROP_ORIGIN, _DEFAULT_CROP_SIZE = 50719, 50720
_MAKE, _MODEL, _ORIENTATION = 271, 272, 274
_XMP = 700
_PHOTOMETRIC_CFA, _PHOTOMETRIC_LINEAR_RAW = 32803, 34892
_ILLUMINANT_D65 = 21

#: Tag colore copiati così come sono da un DNG di origine.
_COLOR_TAGS = (
    50721, 50722,  # ColorMatrix1/2
    50723, 50724,  # CameraCalibration1/2
    50727,  # AnalogBalance
    50728,  # AsShotNeutral
    50778, 50779,  # CalibrationIlluminant1/2
    50964, 50965,  # ForwardMatrix1/2
)

# Matrice XYZ (D65) -> sRGB lineare.
XYZ_TO_SRGB = np.array(
    [[3.2404542, -1.5371385, -0.4985314], [-0.9692660, 1.8760108, 0.0415560], [0.0556434, -0.2040259, 1.0572252]]
)

#: Orientamento LibRaw -> orientamento EXIF/TIFF.
FLIP_TO_ORIENTATION = {0: 1, 3: 3, 5: 8, 6: 6}
_FLIP_TO_ORIENTATION = FLIP_TO_ORIENTATION


@dataclass
class SensorData:
    """Dati del sensore Bayer e informazioni necessarie a descriverli in un DNG."""

    cfa: np.ndarray  # (H, W) uint16, area visibile
    pattern: np.ndarray  # (2, 2): 0 = rosso, 1 = verde, 2 = blu
    black: np.ndarray  # (2, 2) livello del nero per posizione
    white: float
    xyz_to_camera: np.ndarray | None  # (3, 3), illuminante D65
    camera_neutral: np.ndarray | None  # (3,) AsShotNeutral
    flip: int
    source_color_tags: list[tuple] | None = None  # tag copiati da un DNG di origine
    source_baseline_exposure: float = 0.0  # BaselineExposure del DNG di origine (EV)


def _srational(values, denominator: int = 10000) -> tuple[int, ...]:
    out: list[int] = []
    for v in np.asarray(values, dtype=float).ravel():
        out += [int(round(v * denominator)), denominator]
    return tuple(out)


def _rational(values, denominator: int = 1_000_000) -> tuple[int, ...]:
    out: list[int] = []
    for v in np.asarray(values, dtype=float).ravel():
        out += [max(0, int(round(v * denominator))), denominator]
    return tuple(out)


def color_tags_from_dng_bytes(data: bytes) -> list[tuple] | None:
    """Legge i tag colore da un DNG (in memoria); None se non è un DNG con matrici colore."""
    try:
        with tifffile.TiffFile(io.BytesIO(data)) as tif:
            tags = tif.pages[0].tags
            if _COLOR_MATRIX1 not in tags:
                return None
            out = []
            for code in _COLOR_TAGS:
                if code in tags:
                    tag = tags[code]
                    value = tag.value if isinstance(tag.value, tuple) else (tag.value,)
                    out.append((code, int(tag.dtype), tag.count, value, True))
            return out
    except Exception:
        return None


def baseline_exposure_from_dng_bytes(data: bytes) -> float:
    """BaselineExposure di un DNG (0 se assente): i DNG di Adobe DNG Converter lo usano sempre."""
    try:
        with tifffile.TiffFile(io.BytesIO(data)) as tif:
            tag = tif.pages[0].tags.get(_BASELINE_EXPOSURE)
            if tag is None:
                return 0.0
            value = tag.value
            num, den = (value[0], value[1]) if isinstance(value, tuple) else (value, 1)
            return float(num) / float(den or 1)
    except Exception:
        return 0.0


def _crop_tags(crop: tuple[int, int, int, int] | None) -> list[tuple]:
    """Ritaglio (x, y, larghezza, altezza) nelle coordinate dei dati salvati: DefaultCropOrigin/Size."""
    if crop is None:
        return []
    x, y, w, h = (int(v) for v in crop)
    return [(_DEFAULT_CROP_ORIGIN, "I", 2, (x, y), True), (_DEFAULT_CROP_SIZE, "I", 2, (w, h), True)]


def _common_tags(make: str, model: str, baseline_exposure: float, xmp: bytes | None = None) -> list[tuple]:
    unique = f"{make} {model}".strip() or "Denoiser"
    tags = [
        (_DNG_VERSION, "B", 4, (1, 4, 0, 0), True),
        (_DNG_BACKWARD, "B", 4, (1, 3, 0, 0), True),
        (_UNIQUE_MODEL, "s", 0, unique, True),
    ]
    if make:
        tags.append((_MAKE, "s", 0, make, True))
    if model:
        tags.append((_MODEL, "s", 0, model, True))
    if baseline_exposure:
        tags.append((_BASELINE_EXPOSURE, "2i", 1, _srational([baseline_exposure], 100), True))
    if xmp:  # impostazioni di Camera Raw (vedi develop.camera_raw_xmp)
        tags.append((_XMP, "B", len(xmp), xmp, True))
    return tags


def write_bayer_dng(
    path: str | Path,
    sensor: SensorData,
    make: str = "",
    model: str = "",
    baseline_exposure: float = 0.0,
    description: str | None = None,
    xmp: bytes | None = None,
    orientation: int | None = None,
    crop: tuple[int, int, int, int] | None = None,
) -> Path:
    """``orientation``: codice EXIF (predefinito: quello della fotocamera); ``crop``: DefaultCrop.

    ``sensor.camera_neutral``, se c'è, è il bilanciamento con cui aprire il DNG: sostituisce quello
    del DNG d'origine."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tags = _common_tags(make, model, baseline_exposure, xmp)
    tags += [
        (_ORIENTATION, "H", 1, (orientation or _FLIP_TO_ORIENTATION.get(sensor.flip, 1),), True),
        (_CFA_REPEAT_DIM, "H", 2, (2, 2), True),
        (_CFA_PATTERN, "B", 4, tuple(int(v) for v in sensor.pattern.ravel()), True),
        (_BLACK_REPEAT_DIM, "H", 2, (2, 2), True),
        (_BLACK_LEVEL, "2I", 4, _rational(sensor.black.ravel(), 100), True),
        (_WHITE_LEVEL, "I", 1, (int(round(sensor.white)),), True),
    ]
    tags += _crop_tags(crop)
    if sensor.source_color_tags:
        tags += [t for t in sensor.source_color_tags if t[0] != _AS_SHOT_NEUTRAL or sensor.camera_neutral is None]
        if sensor.camera_neutral is not None:
            tags.append((_AS_SHOT_NEUTRAL, "2I", 3, _rational(sensor.camera_neutral), True))
    else:
        if sensor.xyz_to_camera is None:
            raise ValueError("Matrice colore della fotocamera non disponibile: impossibile scrivere il DNG")
        tags += [
            (_COLOR_MATRIX1, "2i", 9, _srational(sensor.xyz_to_camera), True),
            (_ILLUMINANT1, "H", 1, (_ILLUMINANT_D65,), True),
        ]
        if sensor.camera_neutral is not None:
            tags.append((_AS_SHOT_NEUTRAL, "2I", 3, _rational(sensor.camera_neutral), True))
    tifffile.imwrite(
        path,
        np.ascontiguousarray(sensor.cfa, dtype=np.uint16),
        photometric=_PHOTOMETRIC_CFA,
        subfiletype=0,
        extratags=tags,
        description=description,
        software="Denoiser",
        metadata=None,
    )
    return path


def write_linear_dng(
    path: str | Path,
    linear_rgb: np.ndarray,
    make: str = "",
    model: str = "",
    baseline_exposure: float = 0.0,
    description: str | None = None,
    xmp: bytes | None = None,
    orientation: int | None = None,
    crop: tuple[int, int, int, int] | None = None,
) -> Path:
    """``linear_rgb``: RGB lineare (primari sRGB) già orientato e bilanciato, float in [0, 1].
    ``orientation`` e ``crop``: rotazione/specchio (codice EXIF) e DefaultCrop scelti nello sviluppo."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    data = np.round(np.clip(linear_rgb, 0.0, 1.0) * 65535).astype(np.uint16)
    tags = _common_tags(make, model, baseline_exposure, xmp)
    tags += [
        (_BLACK_LEVEL, "I", 1, (0,), True),
        (_WHITE_LEVEL, "I", 1, (65535,), True),
        (_COLOR_MATRIX1, "2i", 9, _srational(XYZ_TO_SRGB), True),
        (_ILLUMINANT1, "H", 1, (_ILLUMINANT_D65,), True),
        (_AS_SHOT_NEUTRAL, "2I", 3, _rational([1.0, 1.0, 1.0]), True),
    ]
    if orientation and orientation != 1:
        tags.append((_ORIENTATION, "H", 1, (orientation,), True))
    tags += _crop_tags(crop)
    tifffile.imwrite(
        path,
        data,
        photometric=_PHOTOMETRIC_LINEAR_RAW,
        planarconfig="contig",
        subfiletype=0,
        extratags=tags,
        description=description,
        software="Denoiser",
        metadata=None,
    )
    return path


def neutral_from_multipliers(multipliers) -> np.ndarray | None:
    """AsShotNeutral = inverso dei moltiplicatori del bilanciamento del bianco, normalizzato sul verde."""
    m = np.asarray(multipliers, dtype=float)[:3]
    if m.size < 3 or not np.all(np.isfinite(m)) or np.any(m <= 0):
        return None
    neutral = m[1] / m
    return neutral if math.isfinite(neutral.sum()) else None
