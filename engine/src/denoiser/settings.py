"""Impostazioni di elaborazione, serializzabili come preset JSON."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field, fields
from typing import Any

#: Modalità di bilanciamento del bianco per i RAW, con etichetta italiana.
WB_MODES: dict[str, str] = {
    "camera": "Come scattato (consigliato)",
    "auto": "Automatico",
    "daylight": "Luce diurna",
    "temperature": "Personalizzato",
    "custom": "Moltiplicatori R / G / B",
}

#: Curva dei toni che non cambia nulla.
IDENTITY_CURVE: tuple[tuple[float, float], ...] = ((0.0, 0.0), (1.0, 1.0))

#: Algoritmi di demosaicizzazione offerti (tutti disponibili nelle build standard di rawpy).
DEMOSAIC_ALGORITHMS: dict[str, str] = {
    "AHD": "AHD (consigliato)",
    "AAHD": "AAHD (come AHD, meno falsi colori)",
    "DCB": "DCB (dettagli fini)",
    "DHT": "DHT (dettagli fini, ISO bassi)",
    "PPG": "PPG (veloce)",
    "VNG": "VNG (morbido, utile con molto rumore)",
    "LINEAR": "Bilineare (molto veloce, qualità bassa)",
}

#: Momento in cui si applica il denoise ai RAW.
RAW_STAGES: dict[str, str] = {
    "sensor": "Prima dello sviluppo (consigliato)",
    "rgb": "Dopo lo sviluppo",
}

OUTPUT_FORMATS: dict[str, str] = {
    "tiff": "TIFF",
    "jpeg": "JPEG",
    "png": "PNG",
    "dng": "DNG (RAW ripulito)",
}


@dataclass
class DevelopSettings:
    """Sviluppo del RAW (ignorato per JPG/PNG)."""

    wb_mode: str = "camera"
    #: Moltiplicatori R, G, B usati con wb_mode == "custom".
    wb_custom: tuple[float, float, float] = (2.0, 1.0, 1.5)
    #: Temperatura (K) e tinta della luce, usate con wb_mode == "temperature".
    temperature: float = 5500.0
    tint: float = 0.0
    demosaic: str = "AHD"


@dataclass
class DenoiseSettings:
    algorithm: str = "wavelet"
    #: Per i RAW: "sensor" (livello 3, solo sensori Bayer; gli altri ripiegano sul livello 2)
    #: oppure "rgb" (livello 2).
    raw_stage: str = "sensor"
    #: Parametri specifici dell'algoritmo; quelli mancanti assumono il valore predefinito.
    params: dict[str, Any] = field(default_factory=dict)
    #: Moltiplicatore della forza sulla luminanza (0 = nessun denoise). 1 corrisponde al rumore
    #: misurato: è il valore che dà la massima fedeltà, valori più bassi lasciano più grana.
    luma_strength: float = 1.0
    #: Moltiplicatore della forza sul colore.
    chroma_strength: float = 1.0
    #: Frazione di dettaglio originale (luminanza) reintrodotta, 0..1.
    detail: float = 0.0
    #: Soglia per i pixel caldi in multipli del rumore stimato (0 = disattivato).
    hot_pixel_threshold: float = 0.0


@dataclass
class ToneSettings:
    """Esposizione e toni. I cursori vanno da −100 a +100 (0 = nessun effetto), come in Lightroom."""

    #: Luminosità automatica dei RAW (come farebbe la fotocamera).
    auto_brightness: bool = True
    exposure_ev: float = 0.0
    contrast: float = 0.0
    brightness: float = 0.0
    highlights: float = 0.0
    shadows: float = 0.0
    saturation: float = 0.0
    #: Punti (x, y) in [0, 1] della curva dei toni, da sinistra a destra.
    curve: tuple[tuple[float, float], ...] = IDENTITY_CURVE


@dataclass
class OutputSettings:
    format: str = "tiff"
    bit_depth: int = 16
    jpeg_quality: int = 95
    #: Cartella di destinazione: relativa alla cartella della foto originale oppure assoluta.
    folder: str = "denoised"
    #: Modello del nome senza estensione. Segnaposto: {nome}, {metodo}, {iso}, {data}.
    name_template: str = "{nome}_dn"
    overwrite: bool = False
    #: Copia tutti i metadati dell'originale (EXIF, XMP, GPS…) con ExifTool.
    copy_metadata: bool = True
    #: Lato lungo massimo in pixel per TIFF, PNG e JPEG (0 = piena risoluzione; il DNG non si riduce).
    max_side: int = 0
    #: Toglie la posizione GPS dai metadati copiati (es. per il web).
    strip_gps: bool = False
    #: Nei DNG scrive toni e curva come istruzioni di Camera Raw (False = «solo pulizia»).
    camera_raw: bool = True


#: Proporzioni del ritaglio (solo un aiuto dell'interfaccia: il ritaglio vero è ``crop``).
ASPECTS: dict[str, str] = {
    "original": "Originale", "free": "Libero", "3:2": "3:2", "4:3": "4:3", "4:5": "4:5", "1:1": "1:1",
    "16:9": "16:9",
}

#: Rotazioni a passi di 90° in senso orario.
ROTATIONS = (0, 90, 180, 270)


@dataclass
class GeometrySettings:
    """Rotazione, raddrizzamento e ritaglio, applicati dopo lo sviluppo (vedi :mod:`denoiser.geometry`)."""

    rotation: int = 0
    #: Specchio orizzontale.
    flip: bool = False
    #: Raddrizzamento in gradi, positivo in senso orario.
    angle: float = 0.0
    #: Ritaglio (x, y, larghezza, altezza) in frazioni della foto ruotata e raddrizzata.
    crop: tuple[float, float, float, float] = (0.0, 0.0, 1.0, 1.0)
    aspect: str = "original"


@dataclass
class Settings:
    develop: DevelopSettings = field(default_factory=DevelopSettings)
    denoise: DenoiseSettings = field(default_factory=DenoiseSettings)
    tone: ToneSettings = field(default_factory=ToneSettings)
    output: OutputSettings = field(default_factory=OutputSettings)
    geometry: GeometrySettings = field(default_factory=GeometrySettings)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "Settings":
        """Crea le impostazioni da un dizionario, ignorando chiavi sconosciute."""

        """Costruisce le impostazioni da un dizionario (preset, impostazioni salvate).

        I valori di tipo sbagliato o fuori dall'elenco (es. "0,8" come testo, un formato sconosciuto)
        tornano al valore predefinito invece di far fallire l'elaborazione più avanti.
        """
        if not isinstance(data, dict):
            data = {}
        return cls(
            develop=_build(DevelopSettings, data.get("develop")),
            denoise=_build(DenoiseSettings, data.get("denoise")),
            tone=_build(ToneSettings, data.get("tone")),
            output=_build(OutputSettings, data.get("output")),
            geometry=_build(GeometrySettings, data.get("geometry")),
        )


def _choices() -> dict[str, Any]:
    from .algorithms import ALGORITHMS

    return {
        "wb_mode": WB_MODES, "demosaic": DEMOSAIC_ALGORITHMS, "raw_stage": RAW_STAGES,
        "format": OUTPUT_FORMATS, "algorithm": ALGORITHMS, "bit_depth": (8, 16),
        "rotation": ROTATIONS, "aspect": ASPECTS,
    }


#: Limiti dei valori numerici.
_RANGES: dict[str, tuple[float, float]] = {
    "luma_strength": (0.0, 10.0), "chroma_strength": (0.0, 10.0), "detail": (0.0, 1.0),
    "hot_pixel_threshold": (0.0, 100.0), "exposure_ev": (-10.0, 10.0), "jpeg_quality": (1, 100),
    "temperature": (2000.0, 15000.0), "tint": (-150.0, 150.0),
    "contrast": (-100.0, 100.0), "brightness": (-100.0, 100.0), "highlights": (-100.0, 100.0),
    "shadows": (-100.0, 100.0), "saturation": (-100.0, 100.0), "max_side": (0, 30000),
    "angle": (-45.0, 45.0),
}

#: Numero massimo di punti della curva dei toni.
MAX_CURVE_POINTS = 16


def _coerce_curve(value: Any) -> tuple[tuple[float, float], ...]:
    points = []
    for point in value:
        x, y = (float(str(v).replace(",", ".")) for v in point)
        if not (0.0 <= x <= 1.0 and 0.0 <= y <= 1.0):
            raise ValueError("curve")
        points.append((x, y))
    points.sort()
    if not 2 <= len(points) <= MAX_CURVE_POINTS:
        raise ValueError("curve")
    return tuple(points)


def _coerce(name: str, value: Any, default: Any) -> Any:
    """Converte ``value`` nel tipo di ``default``; solleva ValueError se non è possibile."""
    if isinstance(default, bool):
        if isinstance(value, bool):
            return value
        if isinstance(value, (int, float)) and value in (0, 1):
            return bool(value)
        if isinstance(value, str) and value.strip().lower() in ("true", "false", "1", "0", "sì", "si", "no"):
            return value.strip().lower() in ("true", "1", "sì", "si")
        raise ValueError(name)
    if isinstance(default, (int, float)):
        if isinstance(value, bool):
            raise ValueError(name)
        if isinstance(value, str):
            value = float(value.strip().replace(",", "."))  # anche la virgola decimale italiana
        number = float(value)
        if number != number:  # NaN
            raise ValueError(name)
        lo, hi = _RANGES.get(name, (-float("inf"), float("inf")))
        number = min(max(number, lo), hi)
        return int(round(number)) if isinstance(default, int) else number
    if isinstance(default, str):
        if not isinstance(value, str):
            raise ValueError(name)
        return value
    if name == "curve":
        return _coerce_curve(value)
    if name == "crop":
        x, y, w, h = (float(str(v).replace(",", ".")) for v in value)
        if not (0.0 <= x < 1.0 and 0.0 <= y < 1.0 and 0.01 <= w <= 1.0 - x + 1e-6 and 0.01 <= h <= 1.0 - y + 1e-6):
            raise ValueError(name)
        return (x, y, min(w, 1.0 - x), min(h, 1.0 - y))
    if isinstance(default, tuple):  # wb_custom
        values = tuple(float(str(x).replace(",", ".")) for x in value)
        if len(values) != len(default) or any(not (0.01 <= x <= 100) for x in values):
            raise ValueError(name)
        return values
    if isinstance(default, dict):
        if not isinstance(value, dict):
            raise ValueError(name)
        return dict(value)
    return value


def _build(dc_type, values: Any):
    defaults = dc_type()
    if not isinstance(values, dict):
        return defaults
    choices = _choices()
    kwargs: dict[str, Any] = {}
    for f in fields(dc_type):
        if f.name not in values:
            continue
        default = getattr(defaults, f.name)
        try:
            value = _coerce(f.name, values[f.name], default)
        except (ValueError, TypeError):
            continue
        if f.name in choices and value not in choices[f.name]:
            continue
        kwargs[f.name] = value
    return dc_type(**kwargs)
