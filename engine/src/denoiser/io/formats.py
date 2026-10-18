"""Estensioni dei file supportati."""

from __future__ import annotations

from pathlib import Path

RAW_EXTENSIONS = frozenset(
    {
        ".3fr", ".ari", ".arw", ".cap", ".cr2", ".cr3", ".crw", ".dcr", ".dng", ".erf",
        ".fff", ".gpr", ".iiq", ".k25", ".kdc", ".mef", ".mos", ".mrw", ".nef", ".nrw",
        ".orf", ".ori", ".pef", ".raf", ".raw", ".rw2", ".rwl", ".sr2", ".srf", ".srw",
        ".x3f",
    }
)
JPEG_EXTENSIONS = frozenset({".jpg", ".jpeg", ".jpe"})
PNG_EXTENSIONS = frozenset({".png"})
TIFF_EXTENSIONS = frozenset({".tif", ".tiff"})

SUPPORTED_EXTENSIONS = RAW_EXTENSIONS | JPEG_EXTENSIONS | PNG_EXTENSIONS | TIFF_EXTENSIONS

OUTPUT_EXTENSIONS = {"tiff": ".tif", "jpeg": ".jpg", "png": ".png", "dng": ".dng"}


def kind_of(path: Path) -> str | None:
    ext = path.suffix.lower()
    if ext in RAW_EXTENSIONS:
        return "raw"
    if ext in JPEG_EXTENSIONS:
        return "jpeg"
    if ext in PNG_EXTENSIONS:
        return "png"
    if ext in TIFF_EXTENSIONS:
        return "tiff"
    return None


def is_supported(path: Path) -> bool:
    return kind_of(path) is not None


def file_dialog_filter() -> str:
    """Filtro per le finestre di apertura file."""
    raw = " ".join(f"*{e}" for e in sorted(RAW_EXTENSIONS))
    std = " ".join(f"*{e}" for e in sorted(JPEG_EXTENSIONS | PNG_EXTENSIONS | TIFF_EXTENSIONS))
    return f"Immagini ({raw} {std});;RAW ({raw});;JPG/PNG/TIFF ({std});;Tutti i file (*)"
