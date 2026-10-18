"""Modelli neurali disponibili: download dalla fonte originale, verifica e conversione in ONNX.

I pesi non sono inclusi nell'applicazione: vengono scaricati una sola volta dalle release
ufficiali, controllati con un'impronta SHA-256 e convertiti in ONNX sul computer
dell'utente (oppure, per RawNIND, estratti dal pacchetto ONNX pubblicato da darktable).
Da quel momento tutto funziona offline.
"""

from __future__ import annotations

import hashlib
import os
import tempfile
import threading
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import onnx

ProgressFn = Callable[[float, str], None]


@dataclass(frozen=True)
class ModelSpec:
    key: str
    label: str
    url: str
    sha256: str
    size_mb: int
    license: str
    description: str
    #: True se la rete riceve il livello di rumore come input (forza regolabile in modo naturale).
    noise_level_input: bool
    #: Multiplo richiesto per le dimensioni dei tasselli.
    multiple: int
    #: Sovrapposizione tra i tasselli (pixel).
    overlap: int = 32
    #: Il grafo dipende dalla misura del tassello (attenzione su finestre o sull'intero tassello).
    fixed_tile: bool = False
    #: "pth": pesi PyTorch da convertire; "dtmodel": pacchetto ONNX di darktable (zip).
    kind: str = "pth"


MODELS: dict[str, ModelSpec] = {
    "drunet": ModelSpec(
        key="drunet",
        label="DRUNet",
        url="https://github.com/cszn/KAIR/releases/download/v1.0/drunet_color.pth",
        sha256="479abe3c5327dfd10ff54a80ec7d4098ca80752a5c9492cdff31cee430bec4b4",
        size_mb=125,
        license="MIT (KAIR, Kai Zhang)",
        description=(
            "Intelligenza artificiale (Zhang et al., 2021) che riceve il rumore misurato nella foto: "
            "la forza si regola come per gli altri metodi."
        ),
        noise_level_input=True,
        multiple=8,
    ),
    "scunet": ModelSpec(
        key="scunet",
        label="SCUNet",
        url="https://github.com/cszn/KAIR/releases/download/v1.0/scunet_color_real_psnr.pth",
        sha256="fa78899ba2caec9d235a900e91d96c689da71c42029230c2028b00f09f809c2e",
        size_mb=69,
        license="Apache 2.0 (SCUNet, Kai Zhang)",
        description=(
            "Intelligenza artificiale (Zhang et al., 2023) addestrata su rumore reale di fotocamere e "
            "smartphone: stima da sola il rumore; la forza miscela il risultato con l'originale."
        ),
        noise_level_input=False,
        multiple=64,
        overlap=16,
        fixed_tile=True,
    ),
    "restormer": ModelSpec(
        key="restormer",
        label="Restormer",
        url="https://github.com/swz30/Restormer/releases/download/v1.0/real_denoising.pth",
        sha256="4cae18bed8a291b9a5deeeab48756bbe6f61fcf7f31a4cca0969b24608601387",
        size_mb=100,
        license="MIT (Restormer, Syed Waqas Zamir et al.)",
        description=(
            "Intelligenza artificiale (Zamir et al., 2022) addestrata su rumore reale: stima da sola il "
            "rumore e dà un risultato molto pulito; più lenta di DRUNet. La forza miscela il risultato "
            "con l'originale."
        ),
        noise_level_input=False,
        multiple=8,
        fixed_tile=True,
    ),
    "rawnind": ModelSpec(
        key="rawnind",
        label="RawNIND",
        url="https://github.com/darktable-org/darktable-ai/releases/download/release-5.6.0/rawdenoise-nind.dtmodel",
        sha256="d71b5f1e727c85a359e6f74dca9e2016c9d8fc3e2f7ac3e9b347d80ceca969af",
        size_mb=55,
        license="CC BY 4.0 / GPL-3.0 (pesi RawNIND, Benoit Brummer, UCLouvain; ONNX di darktable)",
        description=(
            "Intelligenza artificiale (Brummer e De Vleeschouwer, 2025) addestrata su coppie di foto vere di "
            "reflex e mirrorless: lavora direttamente sui dati del sensore, conserva molto dettaglio ed è "
            "veloce. La forza miscela il risultato con l'originale."
        ),
        noise_level_input=False,
        multiple=8,
        kind="dtmodel",
    ),
}

#: File ONNX contenuti nel pacchetto di RawNIND: Bayer (dati del sensore) e lineare (RGB già sviluppato).
RAWNIND_VARIANTS = {"bayer": "model_bayer.onnx", "linear": "model_linear.onnx"}


class ModelNotAvailableError(RuntimeError):
    """Il modello non è ancora stato scaricato."""

    def __init__(self, spec: ModelSpec):
        super().__init__(
            f"Il modello {spec.label} non è ancora stato scaricato ({spec.size_mb} MB). "
            f"Scaricalo dall'interfaccia o con: denoiser-cli modelli scarica {spec.key}"
        )
        self.spec = spec


def models_dir() -> Path:
    override = os.environ.get("DENOISER_MODELS_DIR")
    if override:
        return Path(override)
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("XDG_CACHE_HOME") or str(Path.home() / ".cache")
    return Path(base) / "Denoiser" / "modelli"


def weights_path(spec: ModelSpec) -> Path:
    return models_dir() / Path(spec.url).name


def onnx_path(spec: ModelSpec, tile: int | None = None, variant: str = "") -> Path:
    suffix = f"_{variant}" if variant else ""
    if tile and spec.fixed_tile:
        suffix += f"_{tile}"
    return models_dir() / f"{spec.key}{suffix}.onnx"


def is_downloaded(spec: ModelSpec) -> bool:
    return weights_path(spec).is_file()


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(spec: ModelSpec, progress: ProgressFn | None = None) -> Path:
    """Scarica i pesi, verifica l'impronta e li salva nella cartella dei modelli."""
    target = weights_path(spec)
    if target.is_file():
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(dir=target.parent, suffix=".part")
    tmp = Path(tmp_name)
    try:
        with os.fdopen(fd, "wb") as out, urllib.request.urlopen(spec.url, timeout=60) as resp:
            total = int(resp.headers.get("Content-Length") or 0)
            done = 0
            while True:
                chunk = resp.read(1 << 20)
                if not chunk:
                    break
                out.write(chunk)
                done += len(chunk)
                if progress is not None and total:
                    progress(done / total, f"Download di {spec.label}: {done >> 20} di {total >> 20} MB")
        actual = _sha256(tmp)
        if actual != spec.sha256:
            raise RuntimeError(
                f"Il file scaricato per {spec.label} non corrisponde all'originale atteso "
                "(impronta SHA-256 diversa): è stato scartato."
            )
        tmp.replace(target)
    finally:
        if tmp.exists():
            tmp.unlink()
    return target


_CONVERT_LOCKS: dict[Path, threading.Lock] = {}
_LOCKS_GUARD = threading.Lock()


def ensure_onnx(spec: ModelSpec, tile: int, progress: ProgressFn | None = None, variant: str = "") -> Path:
    """Restituisce il modello ONNX, convertendolo dai pesi originali alla prima occorrenza.

    Anteprima ed esportazione possono chiederlo insieme: un blocco per file evita due conversioni
    in parallelo, e il file definitivo compare solo quando è completo. ``variant``: per i pacchetti
    con più modelli (RawNIND: "bayer" o "linear").
    """
    path = onnx_path(spec, tile, variant)
    if path.is_file():
        return path
    if not is_downloaded(spec):
        raise ModelNotAvailableError(spec)
    with _LOCKS_GUARD:
        lock = _CONVERT_LOCKS.setdefault(path, threading.Lock())
    with lock:
        if path.is_file():  # convertito da un altro lavoro mentre si aspettava
            return path
        if progress is not None:
            progress(0.0, f"Preparazione di {spec.label} (solo la prima volta)…")
        if spec.kind == "dtmodel":
            model = _extract_dtmodel(weights_path(spec), RAWNIND_VARIANTS[variant])
        else:
            from .architectures import build_drunet, build_restormer, build_scunet
            from .checkpoint import load_state_dict

            state = load_state_dict(weights_path(spec))
            builders = {"drunet": lambda: build_drunet(state), "scunet": lambda: build_scunet(state, tile),
                        "restormer": lambda: build_restormer(state, tile)}
            model = builders[spec.key]()
        onnx.checker.check_model(model)
        fd, tmp_name = tempfile.mkstemp(suffix=".tmp", dir=path.parent)
        os.close(fd)
        tmp = Path(tmp_name)
        try:
            onnx.save_model(model, str(tmp))
            tmp.replace(path)
        finally:
            tmp.unlink(missing_ok=True)
    return path


def _extract_dtmodel(package: Path, member: str) -> onnx.ModelProto:
    """Legge un modello ONNX dal pacchetto di darktable (zip con config.json e i file .onnx)."""
    with zipfile.ZipFile(package) as zf:
        name = next((n for n in zf.namelist() if n.endswith("/" + member) or n == member), None)
        if name is None:
            raise RuntimeError(f"Il pacchetto {package.name} non contiene {member}")
        return onnx.load_model_from_string(zf.read(name))
