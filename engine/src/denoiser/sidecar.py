"""Regolazioni di ogni foto, salvate in un file accanto all'originale (``DSC_0001.NEF.denoiser``).

Il nome è diverso da quelli di Lightroom (``DSC_0001.xmp``) e darktable (``DSC_0001.NEF.xmp``), che
non vengono mai toccati. Si salvano sviluppo, toni e riduzione del rumore, non il salvataggio.
Se la cartella della foto non è scrivibile (es. una scheda protetta), il file va nella cartella dei
dati dell'app.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any, Iterable

from . import __version__
from .settings import Settings

SUFFIX = ".denoiser"
#: Sezioni delle impostazioni che appartengono alla foto.
SECTIONS = ("denoise", "develop", "tone", "geometry")


def sidecar_path(photo: str | Path) -> Path:
    photo = Path(photo)
    return photo.with_name(photo.name + SUFFIX)


def fallback_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    return Path(base) / "Denoiser" / "regolazioni"


def _fallback_path(photo: Path) -> Path:
    key = hashlib.sha1(str(Path(photo).resolve()).lower().encode("utf-8")).hexdigest()[:20]
    return fallback_dir() / f"{Path(photo).name}-{key}{SUFFIX}"


def has_settings(photo: str | Path) -> bool:
    return sidecar_path(photo).is_file() or _fallback_path(Path(photo)).is_file()


def read_photo_settings(photo: str | Path) -> dict[str, Any] | None:
    """Sezioni salvate per la foto ({"denoise": …, "develop": …, "tone": …}), o None."""
    for path in (sidecar_path(photo), _fallback_path(Path(photo))):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        sections = data.get("impostazioni") if isinstance(data, dict) else None
        if isinstance(sections, dict):
            return {k: v for k, v in sections.items() if k in SECTIONS and isinstance(v, dict)}
    return None


def write_photo_settings(photo: str | Path, settings: Settings) -> Path:
    """Salva le regolazioni della foto (scrittura atomica). Restituisce il file scritto."""
    photo = Path(photo)
    full = settings.to_dict()
    data = {
        "programma": f"Denoiser {__version__}",
        "foto": photo.name,
        "impostazioni": {k: full[k] for k in SECTIONS},
    }
    text = json.dumps(data, ensure_ascii=False, indent=1)
    target = sidecar_path(photo)
    try:
        _atomic_write(target, text)
        _fallback_path(photo).unlink(missing_ok=True)  # quello accanto alla foto ha la precedenza
        return target
    except OSError:
        fallback = _fallback_path(photo)
        _atomic_write(fallback, text)
        return fallback


def _atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    try:
        temporary.write_text(text, encoding="utf-8")
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def apply_photo_settings(base: Settings, sections: dict[str, Any] | None) -> Settings:
    """``base`` con al posto delle sezioni della foto quelle salvate (valori non validi ignorati)."""
    merged = base.to_dict()
    for key, value in (sections or {}).items():
        if key in SECTIONS:
            merged[key] = {**merged[key], **value}
    return Settings.from_dict(merged)


def changed_fields(before: dict[str, Any], after: dict[str, Any]) -> list[tuple[str, str]]:
    """Campi (sezione, nome) delle sezioni della foto cambiati tra due ``Settings.to_dict()``."""
    return [(section, key) for section in SECTIONS for key in after[section]
            if before[section].get(key) != after[section][key]]


def copy_fields(target: Settings, source: Settings, fields: Iterable[tuple[str, str]]) -> Settings:
    """``target`` con i campi ``fields`` presi da ``source`` (es. per una raffica di foto)."""
    data, values = target.to_dict(), source.to_dict()
    for section, key in fields:
        data[section][key] = values[section][key]
    # I parametri avanzati dipendono dal metodo: cambiando metodo si portano dietro i suoi.
    if ("denoise", "algorithm") in fields:
        data["denoise"]["params"] = values["denoise"]["params"]
    return Settings.from_dict(data)


__all__ = [
    "SECTIONS", "SUFFIX", "apply_photo_settings", "changed_fields", "copy_fields", "has_settings",
    "read_photo_settings", "sidecar_path", "write_photo_settings",
]
