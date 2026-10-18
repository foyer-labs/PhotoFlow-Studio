"""Misura del rumore dei RAW Bayer con il "gemello di rumore".

Dopo la demosaicizzazione il rumore diventa correlato (forma piccole macchie) e chi lo
misura sull'immagine sviluppata lo scambia in parte per dettaglio, sottovalutandolo
anche di 3-6 volte. Qui invece:

1. si stima il modello var = a * x + b sui dati grezzi del sensore, separando i quattro
   piani del mosaico Bayer: lì il rumore è ancora indipendente pixel per pixel e la
   misura è affidabile;
2. si genera un rumore sintetico con le stesse caratteristiche, lo si aggiunge ai dati
   grezzi e si sviluppa di nuovo il RAW con gli stessi parametri;
3. la differenza tra i due sviluppi è un campione di rumore "puro" che ha attraversato
   esattamente la stessa catena (bilanciamento, demosaicizzazione, matrice colore): da lì
   si ricavano il modello del rumore nell'immagine sviluppata e la sua intensità su
   luminanza e colore.

Funziona con qualunque fotocamera Bayer, senza profili.
"""

from __future__ import annotations

from typing import Callable

import numpy as np
import rawpy

from . import vst
from .bayer import BayerLayout
from .color import rgb_to_opponent
from .noise import (
    NoiseModel,
    NoiseProfile,
    channel_variance_bins,
    fit_lower_envelope,
    fit_model_from_noise,
    robust_std,
    sample_tiles,
)

def estimate_sensor_model(raw: rawpy.RawPy, max_pixels: int = 4_000_000) -> NoiseModel:
    """Modello del rumore sui dati grezzi, in unità normalizzate (0 = nero, 1 = saturazione)."""
    layout = BayerLayout(raw)
    visible = raw.raw_image_visible
    tiles_per_rgb: dict[int, list[np.ndarray]] = {0: [], 1: [], 2: []}
    for (i, j, black), c in zip(layout.planes, layout.rgb):
        span = max(layout.white - black, 1.0)
        plane = (visible[i::2, j::2].astype(np.float32) - black) / span
        tiles_per_rgb[c].extend(sample_tiles(plane, max_pixels // 4))
    # Sui dati grezzi il guadagno (fotoni per unità) e il rumore di lettura sono gli stessi per i tre
    # colori: un solo modello stimato su tutti i piani è molto più robusto di tre stime separate.
    bins = [channel_variance_bins(tiles_per_rgb[c]) for c in range(3)]
    levels = np.concatenate([x[0] for x in bins])
    variances = np.concatenate([x[1] for x in bins])
    weights = np.concatenate([x[2] for x in bins])
    if levels.size == 0:
        return NoiseModel(a=np.zeros(3), b=np.full(3, 1e-8))
    a, b = fit_lower_envelope(levels, variances, weights)
    return NoiseModel(a=np.full(3, a), b=np.full(3, b))


#: Rapporto tra la varianza misurata dal gemello e quella vera, per algoritmo di demosaicizzazione
#: (valori centrali misurati con simulazioni Monte Carlo su DNG sintetici sviluppati con LibRaw).
TWIN_VARIANCE_BIAS = {"LINEAR": 1.0, "AHD": 1.22, "AAHD": 1.25, "PPG": 1.26, "DCB": 1.10, "VNG": 1.10, "DHT": 1.15}


def measure_noise_twin(
    raw: rawpy.RawPy,
    postprocess: Callable[[], np.ndarray],
    developed: np.ndarray,
    seed: int = 1234,
    max_pixels: int = 4_000_000,
    demosaic: str = "LINEAR",
) -> NoiseProfile:
    """Esegue il "gemello di rumore". Attenzione: modifica i dati grezzi di ``raw``.

    Con le demosaicizzazioni adattive (AHD, PPG…) il gemello, costruito attorno all'immagine già
    rumorosa, sovrastima la varianza del rumore: la si corregge con :data:`TWIN_VARIANCE_BIAS`.

    ``postprocess`` sviluppa ``raw`` con gli stessi parametri usati per ``developed``
    (RGB lineare float32 in [0, 1]).
    """
    sensor = estimate_sensor_model(raw, max_pixels)
    layout = BayerLayout(raw)
    black_2x2 = np.zeros((2, 2), dtype=np.float32)
    rgb_idx = np.zeros((2, 2), dtype=np.intp)
    for (i, j, black), c in zip(layout.planes, layout.rgb):
        black_2x2[i, j] = black
        rgb_idx[i, j] = c
    visible = raw.raw_image_visible
    h, w = visible.shape
    reps = (h // 2 + 1, w // 2 + 1)
    black_map = np.tile(black_2x2, reps)[:h, :w]
    idx_map = np.tile(rgb_idx, reps)[:h, :w]
    span = np.maximum(np.float32(layout.white) - black_map, np.float32(1.0))

    x = np.clip((visible.astype(np.float32) - black_map) / span, 0.0, None)
    var = sensor.a.astype(np.float32)[idx_map] * x + sensor.b.astype(np.float32)[idx_map]
    rng = np.random.default_rng(seed)
    noise = rng.standard_normal(visible.shape, dtype=np.float32) * np.sqrt(var).astype(np.float32)
    twin_raw = visible.astype(np.float32) + noise * span
    visible[:] = np.clip(np.round(twin_raw), 0, 65535).astype(visible.dtype)

    twin = postprocess()
    diff = twin - developed

    # Modello del rumore nell'immagine sviluppata, canale per canale.
    tiles_dev = sample_tiles(developed, max_pixels)
    tiles_diff = sample_tiles(diff, max_pixels)
    levels = np.concatenate([t.reshape(-1, 3) for t in tiles_dev])
    noises = np.concatenate([t.reshape(-1, 3) for t in tiles_diff])
    noises = noises / np.float32(np.sqrt(TWIN_VARIANCE_BIAS.get(demosaic, 1.15)))
    a = np.zeros(3)
    b = np.zeros(3)
    for c in range(3):
        a[c], b[c] = fit_model_from_noise(levels[:, c], noises[:, c])
    model = NoiseModel(a=a, b=b)

    # Intensità del rumore su luminanza e colore dopo la trasformata di Anscombe.
    unsaturated = levels.max(axis=1) < 0.95
    base = levels[unsaturated][None]
    stab_diff = vst.forward(base + noises[unsaturated][None], model) - vst.forward(base, model)
    opp = rgb_to_opponent(stab_diff)[0]
    component_sigma = np.array([robust_std(opp[:, i]) for i in range(3)])
    return NoiseProfile(model=model, component_sigma=component_sigma, sensor_model=sensor)
