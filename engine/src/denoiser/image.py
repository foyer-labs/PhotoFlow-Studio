"""Rappresentazione interna di un'immagine caricata."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path

import numpy as np

from .develop import ColorInfo
from .noise import NoiseModel, NoiseProfile


class Domain(str, Enum):
    """Spazio in cui si trovano i valori dei pixel."""

    #: RGB lineare (proporzionale alla luce), ottenuto da un RAW: il rumore segue
    #: il modello Poisson-gaussiano e si può stabilizzare con la trasformata di Anscombe.
    LINEAR = "linear"
    #: RGB già codificato per la visualizzazione (sRGB con gamma), es. JPG e PNG.
    DISPLAY = "display"


@dataclass
class ImageMeta:
    """Informazioni sull'origine dell'immagine."""

    source: Path
    #: "raw", "dng-convertito", "jpeg", "png", "tiff"
    kind: str
    camera: str = ""
    make: str = ""
    model: str = ""
    iso: float | None = None
    #: Data di scatto AAAAMMGG, se nota.
    date: str = ""
    bits_per_sample: int = 8
    #: EXIF grezzo dei JPG di origine, riportato nei JPG di uscita.
    exif_bytes: bytes | None = None
    #: Moltiplicatori del bilanciamento del bianco applicati (R, G, B), se noti.
    wb_multipliers: tuple[float, float, float] | None = None
    #: Pattern CFA del sensore (es. "RGGB"), None se non Bayer o sconosciuto.
    cfa_pattern: str | None = None
    #: Profilo colore ICC dell'originale (JPG/PNG/TIFF), reincorporato nel file di uscita.
    icc_profile: bytes | None = None
    #: Note da mostrare all'utente (es. conversione tramite Adobe DNG Converter).
    notes: list[str] = field(default_factory=list)
    #: Colori dello sviluppo (solo RAW con matrice colore), per il bilanciamento del bianco.
    color: ColorInfo | None = None


@dataclass
class RawSource:
    """Quanto serve per ripulire i dati grezzi del sensore e risvilupparli (livello 3)."""

    #: Contenuto del file RAW (si riapre con LibRaw a ogni elaborazione).
    data: bytes
    #: Parametri di sviluppo passati a rawpy.postprocess (senza rotazione).
    params: dict
    #: Orientamento LibRaw: 0 nessuno, 3 = 180°, 5 = 90° antiorario, 6 = 90° orario.
    flip: int
    #: Modello del rumore misurato sui dati grezzi, per canale R, G, B (unità normalizzate).
    sensor_model: NoiseModel


@dataclass
class ImageData:
    """Immagine RGB float32 in [0, 1], forma (H, W, 3)."""

    pixels: np.ndarray
    domain: Domain
    meta: ImageMeta
    #: Canale alfa opzionale (H, W) in [0, 1], ripristinato in uscita se il formato lo supporta.
    alpha: np.ndarray | None = None
    #: Rumore misurato durante lo sviluppo del RAW, se disponibile.
    noise_profile: NoiseProfile | None = None
    #: Dati grezzi per il denoise prima della demosaicizzazione (solo sensori Bayer).
    raw_source: RawSource | None = None

    def __post_init__(self) -> None:
        if self.pixels.ndim != 3 or self.pixels.shape[2] != 3:
            raise ValueError(f"Attesa immagine (H, W, 3), ricevuto {self.pixels.shape}")
        if self.pixels.dtype != np.float32:
            self.pixels = self.pixels.astype(np.float32)

    @property
    def height(self) -> int:
        return self.pixels.shape[0]

    @property
    def width(self) -> int:
        return self.pixels.shape[1]
