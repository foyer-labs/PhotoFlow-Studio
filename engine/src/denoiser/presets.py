"""Preset: impostazioni salvate in JSON, riusabili su una foto o su una cartella intera.

Un preset contiene solo le sezioni (e i campi) che dichiara: applicandolo si cambiano
quelli e il resto delle impostazioni correnti resta com'è. I preset predefiniti
cambiano solo la riduzione del rumore; quelli dell'utente le sezioni scelte al salvataggio.
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any

from .settings import Settings

PRESET_SUFFIX = ".json"

#: Nome -> (descrizione, contenuto parziale).
BUILTIN_PRESETS: dict[str, tuple[str, dict[str, Any]]] = {
    "Bilanciato": (
        "Toglie il rumore misurato: la scelta giusta quasi sempre.",
        {"denoise": {"algorithm": "wavelet", "luma_strength": 1.0, "chroma_strength": 1.0, "detail": 0.0}},
    ),
    "Delicato": (
        "Lascia un po' di grana naturale e più dettaglio.",
        {"denoise": {"algorithm": "wavelet", "luma_strength": 0.6, "chroma_strength": 0.9, "detail": 0.15}},
    ),
    "Forte (ISO alti)": (
        "Per foto notturne o a ISO molto alti; più lento.",
        {"denoise": {"algorithm": "nlm", "luma_strength": 1.3, "chroma_strength": 1.5, "detail": 0.0}},
    ),
    "Solo colore": (
        "Elimina le macchie colorate senza toccare la grana.",
        {"denoise": {"algorithm": "wavelet", "luma_strength": 0.0, "chroma_strength": 1.2, "detail": 0.0}},
    ),
    "Neurale RAW (DRUNet)": (
        "Intelligenza artificiale: in genere la qualità più alta. Lenta senza scheda video.",
        {"denoise": {"algorithm": "drunet", "luma_strength": 1.0, "chroma_strength": 1.0, "detail": 0.0}},
    ),
    "Neurale JPG (SCUNet)": (
        "Intelligenza artificiale addestrata su foto reali: ideale per JPG e smartphone.",
        {"denoise": {"algorithm": "scunet", "luma_strength": 1.0, "chroma_strength": 1.0, "detail": 0.0}},
    ),
    "Neurale RAW naturale (RawNIND)": (
        "Intelligenza artificiale addestrata su RAW veri di reflex e mirrorless: più dettaglio, aspetto "
        "naturale, veloce.",
        {"denoise": {"algorithm": "rawnind", "luma_strength": 1.0, "chroma_strength": 1.0, "detail": 0.0}},
    ),
    "Neurale molto pulito (Restormer)": (
        "Intelligenza artificiale addestrata su rumore reale: risultato molto pulito. Lenta senza scheda video.",
        {"denoise": {"algorithm": "restormer", "luma_strength": 1.0, "chroma_strength": 1.0, "detail": 0.0}},
    ),
}

# Ogni preset predefinito riporta i RAW a «Prima dello sviluppo»: è la fase consigliata per tutti i metodi
# (dopo lo sviluppo il rumore è correlato tra pixel vicini e DRUNet, per esempio, ne toglie poco).
for _description, _content in BUILTIN_PRESETS.values():
    _content["denoise"].setdefault("raw_stage", "sensor")

#: Sezioni che un preset dell'utente può contenere, con etichetta italiana.
SECTIONS = {"denoise": "Riduzione del rumore", "develop": "Sviluppo RAW", "tone": "Luminosità", "output": "Salvataggio"}

_INVALID = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


class PresetError(ValueError):
    pass


def user_presets_dir() -> Path:
    base = os.environ.get("APPDATA") or os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "Denoiser" / "preset"


def clean_name(name: str) -> str:
    """Nome valido come nome di file (caratteri non ammessi sostituiti)."""
    cleaned = _INVALID.sub("_", name).strip(" .")
    if not cleaned:
        raise PresetError("Il nome del preset è vuoto.")
    return cleaned


def preset_path(name: str, folder: Path | None = None) -> Path:
    return (folder or user_presets_dir()) / (clean_name(name) + PRESET_SUFFIX)


def save_preset(settings: Settings, path: str | Path, sections: tuple[str, ...] | None = None) -> Path:
    """Salva le sezioni indicate (tutte se None) nel file ``path`` (estensione .json aggiunta se manca)."""
    path = Path(path)
    if path.suffix.lower() != PRESET_SUFFIX:
        path = path.with_name(path.name + PRESET_SUFFIX)  # non with_suffix: "v1.5" resterebbe "v1"
    data = settings.to_dict()
    if sections is not None:
        data = {k: v for k, v in data.items() if k in sections}
    if "output" in data:
        data["output"]["overwrite"] = False  # mai sovrascrivere in silenzio per effetto di un preset
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    return path


def read_preset(path: str | Path) -> dict[str, Any]:
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise PresetError(f"Impossibile leggere il preset {Path(path).name}: {exc}") from exc
    if not isinstance(data, dict):
        raise PresetError(f"Il file {Path(path).name} non è un preset valido.")
    return data


def apply_preset(current: Settings, preset: dict[str, Any]) -> Settings:
    """Applica un preset (anche parziale) alle impostazioni correnti, senza modificarle."""
    merged = current.to_dict()
    for section, values in preset.items():
        if section in merged and isinstance(values, dict):
            if section == "denoise" and values.get("algorithm") not in (None, merged["denoise"]["algorithm"]):
                merged["denoise"]["params"] = {}  # i parametri sono specifici dell'algoritmo
            merged[section].update(values)
    return Settings.from_dict(merged)


def load_preset(path: str | Path) -> Settings:
    """Preset di un file applicato alle impostazioni predefinite."""
    return apply_preset(Settings(), read_preset(path))


def list_presets() -> dict[str, tuple[str, dict[str, Any] | Path]]:
    """Nome -> (descrizione, contenuto per i predefiniti oppure percorso per quelli dell'utente)."""
    result: dict[str, tuple[str, dict[str, Any] | Path]] = {
        name: (desc, data) for name, (desc, data) in BUILTIN_PRESETS.items()
    }
    folder = user_presets_dir()
    if folder.is_dir():
        for p in sorted(folder.glob(f"*{PRESET_SUFFIX}")):
            name = p.stem if p.stem not in BUILTIN_PRESETS else f"{p.stem} (mio)"
            result[name] = ("Preset salvato da te.", p)
    return result


def preset_content(entry: dict[str, Any] | Path) -> dict[str, Any]:
    return read_preset(entry) if isinstance(entry, Path) else entry


def resolve_preset(name_or_path: str, base: Settings | None = None) -> Settings:
    """Accetta il nome di un preset (predefinito o dell'utente) oppure il percorso di un file JSON."""
    base = base or Settings()
    candidate = Path(name_or_path)
    if candidate.suffix.lower() == PRESET_SUFFIX and candidate.is_file():
        return apply_preset(base, read_preset(candidate))
    available = list_presets()
    for name, (_, entry) in available.items():
        if name.lower() == name_or_path.lower():
            return apply_preset(base, preset_content(entry))
    raise PresetError(f"Preset «{name_or_path}» non trovato. Disponibili: {', '.join(available)}")
