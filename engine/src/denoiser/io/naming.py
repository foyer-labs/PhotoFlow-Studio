"""Percorso e nome dei file di uscita, secondo le impostazioni configurabili."""

from __future__ import annotations

import re
from pathlib import Path

from ..image import ImageMeta
from ..settings import OutputSettings
from .formats import OUTPUT_EXTENSIONS

PLACEHOLDERS = {
    "nome": "nome del file originale senza estensione",
    "metodo": "algoritmo di denoise usato",
    "iso": "sensibilità ISO (se nota)",
    "data": "data di scatto AAAAMMGG (se nota)",
}

_INVALID = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


class _Defaults(dict):
    def __missing__(self, key: str) -> str:  # segnaposto sconosciuti restano visibili
        return "{" + key + "}"


def render_name(template: str, meta: ImageMeta, method: str, extra: dict[str, str] | None = None) -> str:
    """``extra``: altri segnaposto dati da chi esporta (PhotoFlow Studio: {evento}, {stelle}, {seq})."""
    values = _Defaults(
        nome=meta.source.stem,
        metodo=method,
        iso=str(int(meta.iso)) if meta.iso else "",
        data=meta.date,
    )
    values.update({k: str(v) for k, v in (extra or {}).items() if k not in values})
    try:
        name = template.format_map(values)
    except (ValueError, IndexError, AttributeError, KeyError, TypeError):  # es. «{nome.x}», «{0}»
        name = template
    name = _INVALID.sub("_", name).strip(" .")
    return name or meta.source.stem


def output_path(meta: ImageMeta, out: OutputSettings, method: str, extra: dict[str, str] | None = None) -> Path:
    folder = Path(out.folder) if out.folder else Path(".")
    if not folder.is_absolute():
        folder = meta.source.parent / folder
    return folder / (render_name(out.name_template, meta, method, extra) + OUTPUT_EXTENSIONS[out.format])


def unique_path(path: Path) -> Path:
    """Se il file esiste già aggiunge _1, _2, … al nome."""
    if not path.exists():
        return path
    i = 1
    while True:
        candidate = path.with_name(f"{path.stem}_{i}{path.suffix}")
        if not candidate.exists():
            return candidate
        i += 1
