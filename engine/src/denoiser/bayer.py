"""Livello 3: denoise sui dati grezzi del sensore Bayer, prima della demosaicizzazione.

Il mosaico Bayer viene separato nei quattro piani R, G1, G2, B (a metà risoluzione).
In ciascun piano il rumore è indipendente pixel per pixel e il suo modello è noto con
precisione, quindi dopo la trasformata di Anscombe vale esattamente 1: la forza 1
corrisponde davvero al rumore presente. I quattro piani vengono poi combinati in
una componente di luminanza e tre di colore (trasformazione ortonormale), così che
luminanza e colore si possano dosare separatamente come al livello 2.
Infine i dati ripuliti vengono riscritti nel RAW e sviluppati da LibRaw.
"""

from __future__ import annotations

import io
from typing import TYPE_CHECKING, Callable

import cv2
import numpy as np
import rawpy

from .algorithms import Denoiser
from .noise import NoiseModel
from .settings import DenoiseSettings

if TYPE_CHECKING:
    from .io.dng import SensorData

# Righe: luminanza, rosso-blu, verde-magenta, differenza tra i due verdi.
# Colonne: piani nell'ordine R, G1, G2, B.
_PACKED = np.array(
    [
        [0.5, 0.5, 0.5, 0.5],
        [1 / np.sqrt(2), 0.0, 0.0, -1 / np.sqrt(2)],
        [-0.5, 0.5, 0.5, -0.5],
        [0.0, 1 / np.sqrt(2), -1 / np.sqrt(2), 0.0],
    ],
    dtype=np.float32,
)


# ---------------------------------------------------------------- orientamento


def orient(arr: np.ndarray, flip: int) -> np.ndarray:
    """Applica l'orientamento LibRaw a un'immagine nell'orientamento del sensore."""
    if flip == 3:
        return arr[::-1, ::-1]
    if flip == 5:
        return np.rot90(arr, 1)
    if flip == 6:
        return np.rot90(arr, -1)
    return arr


def display_rect_to_sensor(
    rect: tuple[int, int, int, int], flip: int, sensor_w: int, sensor_h: int
) -> tuple[int, int, int, int]:
    """Converte un rettangolo (x, y, w, h) dell'immagine orientata in coordinate del sensore."""
    x, y, w, h = rect
    if flip == 3:
        return sensor_w - (x + w), sensor_h - (y + h), w, h
    if flip == 6:  # immagine = rot90 orario del sensore
        return y, sensor_h - (x + w), h, w
    if flip == 5:  # immagine = rot90 antiorario del sensore
        return sensor_w - (y + h), x, h, w
    return x, y, w, h


# ---------------------------------------------------------------- piani Bayer


class BayerLayout:
    """Posizione dei quattro piani nel pattern 2x2 e livelli di nero/bianco."""

    def __init__(self, raw: rawpy.RawPy):
        colors = raw.raw_colors_visible[:2, :2]
        desc = raw.color_desc.decode("ascii", "replace")
        blacks = np.asarray(raw.black_level_per_channel, dtype=np.float64)
        self.white = float(raw.white_level)
        offsets: dict[str, list[tuple[int, int, float]]] = {"R": [], "G": [], "B": []}
        for i in range(2):
            for j in range(2):
                offsets[desc[colors[i, j]]].append((i, j, float(blacks[colors[i, j]])))
        if len(offsets["R"]) != 1 or len(offsets["G"]) != 2 or len(offsets["B"]) != 1:
            raise ValueError("Il sensore non ha un pattern Bayer standard")
        # Ordine: R, G1, G2, B; rgb indica quale modello di rumore usare.
        self.planes = [offsets["R"][0], offsets["G"][0], offsets["G"][1], offsets["B"][0]]
        self.rgb = [0, 1, 1, 2]
        # Moltiplicatori del bilanciamento del bianco per piano (verde = 1).
        wb = np.asarray(raw.camera_whitebalance, dtype=np.float64)[:3]
        if not np.all(np.isfinite(wb)) or np.any(wb <= 0):
            wb = np.asarray(raw.daylight_whitebalance, dtype=np.float64)[:3]
        if not np.all(np.isfinite(wb)) or np.any(wb <= 0):
            wb = np.ones(3)
        wb = wb / wb[1]
        self.wb = np.array([wb[c] for c in self.rgb])

    def weights(self) -> np.ndarray:
        """Pesi per piano che rendono un grigio neutro uguale sui quattro piani dopo la trasformata.

        La GAT porta i piani in «unità di fotoni»: prima del bilanciamento del bianco un grigio ha
        meno fotoni in R e B che in G, e i suoi bordi finirebbero nelle componenti di colore (aloni
        colorati dopo il denoise del colore). Con il peso √m_k il grigio torna neutro; il rumore del
        piano k vale allora w_k. Normalizzati in modo che la luminanza abbia rumore 1.
        """
        w = np.sqrt(np.clip(self.wb, 0.25, 4.0))
        return (w / np.sqrt(np.mean(w**2))).astype(np.float32)


def _stabilize(x: np.ndarray, a: float, b: float) -> np.ndarray:
    # Senza la costante del segnale nullo: con a ≈ 0 sarebbe enorme e schiaccerebbe la dinamica.
    a = max(a, 1e-7)
    offset = 2.0 * np.sqrt(0.375 + b / a**2)
    return (2.0 * np.sqrt(np.maximum(x / a + 0.375 + b / a**2, 0.0)) - offset).astype(np.float32)


def _unstabilize(d: np.ndarray, a: float, b: float) -> np.ndarray:
    from .vst import exact_unbiased_inverse

    a = max(a, 1e-7)
    sigma2 = b / a**2
    return (exact_unbiased_inverse(d + np.float32(2.0 * np.sqrt(0.375 + sigma2)), sigma2) * np.float32(a)).astype(np.float32)


def denoise_sensor_region(
    raw: rawpy.RawPy,
    region: tuple[int, int, int, int] | None,
    model: NoiseModel,
    algorithm: Denoiser,
    settings: DenoiseSettings,
    progress: Callable[[float, str], None] | None = None,
) -> None:
    """Ripulisce in place i dati grezzi di ``raw`` nella regione (x, y, w, h) del sensore."""
    layout = BayerLayout(raw)
    visible = raw.raw_image_visible
    full_h, full_w = visible.shape
    if region is None:
        x0, y0, x1, y1 = 0, 0, full_w, full_h
    else:
        x, y, w, h = region
        pad = 2 * algorithm.margin
        # Origine allineata a 128 pixel del sensore (64 per piano): la scomposizione wavelet e i
        # tasselli cadono come nella foto intera, così l'anteprima coincide con l'esportazione.
        x0, y0 = max(0, x - pad) & ~127, max(0, y - pad) & ~127
        x1, y1 = min(full_w, x + w + pad), min(full_h, y + h + pad)
    # Dimensioni pari, così ogni piano ha la stessa forma.
    x1 -= (x1 - x0) % 2
    y1 -= (y1 - y0) % 2
    if x1 - x0 < 4 or y1 - y0 < 4:
        return
    block = visible[y0:y1, x0:x1]

    stab = []
    norm = []
    for (i, j, black), c in zip(layout.planes, layout.rgb):
        span = max(layout.white - black, 1.0)
        plane = (block[i::2, j::2].astype(np.float32) - black) / span
        norm.append(plane)
        stab.append(_stabilize(plane, float(model.a[c]), float(model.b[c])))
    stacked = np.stack(stab, axis=-1)

    if settings.hot_pixel_threshold > 0:
        stacked = _remove_hot_pixels(stacked, settings.hot_pixel_threshold)

    if getattr(algorithm, "raw_model", False):
        # Reti addestrate sui dati grezzi (RawNIND): niente trasformata, lavorano sul mosaico normalizzato.
        if settings.hot_pixel_threshold > 0:
            # Solo i pixel caldi sostituiti (con la mediana dei vicini, un valore già «pulito») tornano
            # dalla trasformata; gli altri restano i dati originali, senza passare per l'inversa.
            stab_all = np.stack(stab, axis=-1)
            norm = [
                np.where(stacked[..., k] != stab_all[..., k],
                         _unstabilize(stacked[..., k], float(model.a[c]), float(model.b[c])), norm[k])
                for k, c in enumerate(layout.rgb)
            ]
        planes = _raw_model_denoise(norm, layout, algorithm, settings, progress)
        for (i, j, black), plane in zip(layout.planes, planes):
            span = max(layout.white - black, 1.0)
            block[i::2, j::2] = np.clip(np.round(plane * span + black), 0, 65535).astype(visible.dtype)
        return

    weights = layout.weights()
    stacked = stacked * weights
    if algorithm.joint:
        stacked = _joint_sensor_denoise(stacked, model, layout, algorithm, settings, progress)
    else:
        stacked = _packed_denoise(stacked, weights, algorithm, settings, progress, min(full_h, full_w) // 2)
    stacked = stacked / weights

    for k, ((i, j, black), c) in enumerate(zip(layout.planes, layout.rgb)):
        span = max(layout.white - black, 1.0)
        plane = _unstabilize(stacked[..., k], float(model.a[c]), float(model.b[c]))
        block[i::2, j::2] = np.clip(np.round(plane * span + black), 0, 65535).astype(visible.dtype)


def _packed_denoise(
    stacked: np.ndarray,
    weights: np.ndarray,
    algorithm: Denoiser,
    settings: DenoiseSettings,
    progress: Callable[[float, str], None] | None,
    full_size: int | None = None,
) -> np.ndarray:
    """Algoritmi classici: componenti ortonormali dei quattro piani, ripulite una alla volta.

    ``full_size``: lato minore dei piani interi (scelte automatiche uguali in anteprima ed esportazione).
    """
    comps = stacked @ _PACKED.T
    params = dict(settings.params, _full_size=full_size) if full_size else settings.params
    # Rumore di ogni componente: i piani pesati hanno rumore w_k (indipendente tra i piani).
    sigmas = np.sqrt((_PACKED.astype(np.float64) ** 2) @ (weights.astype(np.float64) ** 2))
    strengths = (settings.luma_strength, settings.chroma_strength, settings.chroma_strength, settings.chroma_strength)
    names = ("luminanza", "colore rosso-blu", "colore verde-magenta", "equilibrio dei verdi")
    for k, strength in enumerate(strengths):
        if progress is not None:
            progress(k / 4, f"Denoise sensore: {names[k]}")
        if strength <= 0:
            continue
        original = np.ascontiguousarray(comps[..., k])
        cleaned = algorithm.denoise(original, float(strength * sigmas[k]), params)
        if k == 0 and settings.detail > 0:
            cleaned = cleaned + settings.detail * (original - cleaned)
        comps[..., k] = cleaned
    return comps @ _PACKED  # trasformazione ortonormale: l'inversa è la trasposta


def _raw_model_denoise(
    norm: list[np.ndarray],
    layout: "BayerLayout",
    algorithm: Denoiser,
    settings: DenoiseSettings,
    progress: Callable[[float, str], None] | None,
) -> list[np.ndarray]:
    """Rete sui dati grezzi (RawNIND): ``norm`` sono i piani R, G1, G2, B (nero = 0, bianco = 1).

    La rete vuole un mosaico RGGB: se il sensore ha un altro ordine si parte dal primo fotosito rosso
    (la prima riga o colonna resta com'è). Il risultato si dosa come per le altre reti «cieche»:
    luminanza e colore separati, con forza massima 1 e recupero del dettaglio sulla luminanza.
    """
    h, w = norm[0].shape
    mosaic = np.empty((2 * h, 2 * w), dtype=np.float32)
    for (i, j, _black), plane in zip(layout.planes, norm):
        mosaic[i::2, j::2] = plane
    ri, rj, _ = layout.planes[0]
    sub_h, sub_w = (2 * h - ri) & ~1, (2 * w - rj) & ~1
    sub = mosaic[ri : ri + sub_h, rj : rj + sub_w]
    packed = np.stack([sub[0::2, 0::2], sub[0::2, 1::2], sub[1::2, 0::2], sub[1::2, 1::2]])
    cleaned = algorithm.denoise_mosaic(packed, settings.params, progress)

    # Miscela in luminanza e colore (piani bilanciati, così un grigio resta nella sola luminanza).
    # Pesi del bilanciamento nell'ordine R, G, G, B del mosaico RGGB.
    wb = np.array([layout.wb[0], 1.0, 1.0, layout.wb[3]], dtype=np.float32)[:, None, None]
    residual = np.tensordot(_PACKED, (cleaned - packed) * wb, axes=(1, 0))
    luma = min(settings.luma_strength, 1.0) * (1.0 - min(max(settings.detail, 0.0), 1.0))
    chroma = min(settings.chroma_strength, 1.0)
    residual *= np.array([luma, chroma, chroma, chroma], dtype=np.float32)[:, None, None]
    packed = packed + np.tensordot(_PACKED.T, residual, axes=(1, 0)) / wb

    sub[0::2, 0::2], sub[0::2, 1::2], sub[1::2, 0::2], sub[1::2, 1::2] = packed
    return [mosaic[i::2, j::2] for i, j, _black in layout.planes]


def _joint_sensor_denoise(
    stacked: np.ndarray,
    model: NoiseModel,
    layout: "BayerLayout",
    algorithm: Denoiser,
    settings: DenoiseSettings,
    progress: Callable[[float, str], None] | None,
) -> np.ndarray:
    """Reti neurali: i piani diventano un'immagine RGB a metà risoluzione (verde = media dei due verdi).

    La differenza tra i due verdi (dettaglio fine e squilibrio dei verdi) viene ripulita a parte
    con la wavelet.
    """
    from .algorithms import get_algorithm
    from .joint import joint_denoise

    from .color import _OPPONENT

    w = layout.weights().astype(np.float64)
    scale = max(
        float(w[k] * _stabilize(np.float32(1.0), float(model.a[c]), float(model.b[c])))
        for k, c in enumerate(layout.rgb)
    )
    # Le reti «cieche» (SCUNet) rendono meglio se il nero non è esattamente a 0 (come nelle foto su
    # cui sono addestrate): si rimette la costante della trasformata, limitata al 10% della scala.
    offset = np.zeros(4, dtype=np.float32)
    if algorithm.blind:
        offset = np.array([
            min(w[k] * 2.0 * np.sqrt(0.375 + float(model.b[c]) / max(float(model.a[c]), 1e-7) ** 2), 0.1 * scale)
            for k, c in enumerate(layout.rgb)
        ], dtype=np.float32)
        stacked = stacked + offset
        scale += float(offset.max())
    r, g1, g2, b = (stacked[..., k] for k in range(4))
    rgb = np.stack([r, (g1 + g2) / 2, b], axis=-1) / scale
    diff = (g1 - g2) / np.sqrt(2)
    # Rumore per canale dopo trasformata e pesi (verde = media di due piani), poi per componente.
    channel = np.array([w[0], np.sqrt(w[1] ** 2 + w[2] ** 2) / 2, w[3]])
    component = np.sqrt((_OPPONENT.astype(np.float64) ** 2) @ channel**2) / scale
    cleaned = joint_denoise(algorithm, rgb, component, settings, progress) * scale
    # Con le reti "cieche" la forza è una miscela che non supera 1: lo stesso limite qui.
    diff_strength = min(settings.chroma_strength, 1.0) if algorithm.blind else settings.chroma_strength
    if diff_strength > 0:
        wavelet = get_algorithm("wavelet")
        diff_sigma = float(np.sqrt((w[1] ** 2 + w[2] ** 2) / 2))
        diff = wavelet.denoise(np.ascontiguousarray(diff), float(diff_strength * diff_sigma), {})
    g = cleaned[..., 1]
    return (np.stack(
        [cleaned[..., 0], g + diff / np.sqrt(2), g - diff / np.sqrt(2), cleaned[..., 2]], axis=-1
    ) - offset).astype(np.float32)


def _remove_hot_pixels(stacked: np.ndarray, threshold: float) -> np.ndarray:
    """Nel dominio stabilizzato il rumore vale 1: un pixel isolato oltre la soglia è un pixel caldo.

    Un pixel caldo è un singolo fotosito: i piani vicini non ne risentono. Una stella o un riflesso
    puntiforme, invece, illumina anche i fotositi accanto degli altri colori: se negli altri piani
    l'intorno è anch'esso più chiaro, il punto è vero e si conserva.
    """
    out = stacked.copy()
    kernel = np.ones((3, 3), np.uint8)
    excess, medians = [], []
    for k in range(stacked.shape[-1]):
        plane = np.ascontiguousarray(stacked[..., k])
        med = cv2.medianBlur(plane, 3)
        excess.append(plane - med)
        medians.append(med)
    spread = [cv2.dilate(e, kernel) for e in excess]
    limit = max(3.0, threshold / 3.0)
    for k in range(stacked.shape[-1]):
        others = np.max([spread[j] for j in range(len(excess)) if j != k], axis=0)
        hot = (excess[k] > threshold) & (others < limit)
        out[..., k] = np.where(hot, medians[k], stacked[..., k])
    return out


def develop_denoised(
    data: bytes,
    params: dict,
    model: NoiseModel,
    algorithm: Denoiser,
    settings: DenoiseSettings,
    sensor_region: tuple[int, int, int, int] | None = None,
    progress: Callable[[float, str], None] | None = None,
) -> np.ndarray:
    """Riapre il RAW, ripulisce i dati del sensore e lo sviluppa: RGB lineare float32 (orientamento del sensore)."""
    with rawpy.imread(io.BytesIO(data)) as raw:
        denoise_sensor_region(raw, sensor_region, model, algorithm, settings, progress)
        if progress is not None:
            progress(0.95, "Sviluppo del RAW")
        return raw.postprocess(**params).astype(np.float32) / 65535.0


def denoised_sensor_data(
    data: bytes,
    model: NoiseModel,
    algorithm: Denoiser,
    settings: DenoiseSettings,
    flip: int,
    denoise: bool = True,
    progress: Callable[[float, str], None] | None = None,
) -> "SensorData":
    """Dati del sensore ripuliti (area visibile) e informazioni per scriverli in un DNG Bayer."""
    from .io.dng import SensorData, baseline_exposure_from_dng_bytes, color_tags_from_dng_bytes, neutral_from_multipliers

    with rawpy.imread(io.BytesIO(data)) as raw:
        if denoise:
            denoise_sensor_region(raw, None, model, algorithm, settings, progress)
        layout = BayerLayout(raw)
        colors = raw.raw_colors_visible[:2, :2]
        desc = raw.color_desc.decode("ascii", "replace")
        pattern = np.vectorize(lambda c: {"R": 0, "G": 1, "B": 2}.get(desc[c], 1))(colors)
        blacks = np.asarray(raw.black_level_per_channel, dtype=float)[colors]
        xyz = np.asarray(raw.rgb_xyz_matrix, dtype=float)[:3]
        return SensorData(
            cfa=raw.raw_image_visible.copy(),
            pattern=pattern.astype(np.uint8),
            black=blacks,
            white=layout.white,
            xyz_to_camera=xyz if np.any(xyz) else None,
            camera_neutral=neutral_from_multipliers(raw.camera_whitebalance),
            flip=flip,
            source_color_tags=color_tags_from_dng_bytes(data),
            source_baseline_exposure=baseline_exposure_from_dng_bytes(data),
        )
