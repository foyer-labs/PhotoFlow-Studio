"""Lettura dei metadati essenziali (fotocamera, ISO, data) usati nei nomi dei file.

La copia completa dei metadati nel file di uscita arriverà con ExifTool in una fase successiva.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from PIL import Image

_MAKE, _MODEL = 0x010F, 0x0110
_EXIF_IFD = 0x8769
_ISO, _DATE_ORIGINAL = 0x8827, 0x9003


@dataclass
class BasicExif:
    camera: str = ""
    make: str = ""
    model: str = ""
    iso: float | None = None
    date: str = ""  # AAAAMMGG


def read_basic_exif(path: Path) -> BasicExif:
    """Legge i dati principali; funziona con JPG, PNG, TIFF e con i RAW basati su TIFF (NEF, DNG, ARW, CR2…)."""
    info = _read_with_pillow(path)
    if not info.camera and not info.iso:
        info = _read_with_tifffile(path) or info
    return info


def camera_name(make: str, model: str) -> str:
    """Nome leggibile: «NIKON CORPORATION» + «NIKON D5100» → «NIKON D5100» (marca non ripetuta)."""
    make, model = make.strip(), model.strip()
    brand = make.split()[0].lower() if make else ""
    if not model:
        return make
    if not brand or model.lower().startswith(brand):
        return model
    return f"{make} {model}"


def _finish(make: str, model: str, iso, date: str) -> BasicExif:
    info = BasicExif(make=make, model=model)
    info.camera = camera_name(make, model)
    if isinstance(iso, (tuple, list)):
        iso = iso[0] if iso else None
    try:
        info.iso = float(iso) if iso else None
    except (TypeError, ValueError):
        info.iso = None
    if len(date) >= 10:
        info.date = date[:10].replace(":", "")
    return info


def _clean(value) -> str:
    return str(value or "").strip("\x00 ")


def _read_with_pillow(path: Path) -> BasicExif:
    try:
        with Image.open(path) as im:
            exif = im.getexif()
    except Exception:
        return BasicExif()
    try:
        sub = exif.get_ifd(_EXIF_IFD)
    except Exception:
        sub = {}
    return _finish(_clean(exif.get(_MAKE)), _clean(exif.get(_MODEL)), sub.get(_ISO), _clean(sub.get(_DATE_ORIGINAL)))


def _read_with_tifffile(path: Path) -> BasicExif | None:
    """Per i RAW che Pillow non sa aprire: legge solo le intestazioni TIFF."""
    try:
        import tifffile

        with tifffile.TiffFile(path) as tif:
            tags = tif.pages[0].tags
            make = _clean(tags[_MAKE].value) if _MAKE in tags else ""
            model = _clean(tags[_MODEL].value) if _MODEL in tags else ""
            sub = tags[_EXIF_IFD].value if _EXIF_IFD in tags else {}
            iso = sub.get("ISOSpeedRatings") or sub.get("PhotographicSensitivity") if isinstance(sub, dict) else None
            date = _clean(sub.get("DateTimeOriginal")) if isinstance(sub, dict) else ""
            return _finish(make, model, iso, date)
    except Exception:
        return None
