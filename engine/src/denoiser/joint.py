"""Denoise con algoritmi che elaborano i tre canali insieme (reti neurali).

Le reti ripuliscono l'immagine a colori intera; per mantenere forze separate su
luminanza e colore il risultato viene ricombinato nelle componenti opponenti.
"""

from __future__ import annotations

from typing import Callable

import numpy as np

from .algorithms import Denoiser
from .color import opponent_to_rgb, rgb_to_opponent
from .settings import DenoiseSettings

ProgressFn = Callable[[float, str], None]


def joint_denoise(
    algorithm: Denoiser,
    rgb: np.ndarray,
    component_sigma: np.ndarray,
    settings: DenoiseSettings,
    progress: ProgressFn | None = None,
) -> np.ndarray:
    """``rgb`` (H, W, 3) circa in [0, 1]; ``component_sigma``: rumore di (luminanza, colore 1, colore 2)."""
    ls, cs = settings.luma_strength, settings.chroma_strength
    original = rgb_to_opponent(rgb)
    out = original.copy()
    if algorithm.blind:
        cleaned = rgb_to_opponent(algorithm.denoise_rgb(rgb, 0.0, settings.params, progress))
        residual = cleaned - original
        weights = np.array([min(ls, 1.0), min(cs, 1.0), min(cs, 1.0)], dtype=np.float32)
        # Una rete «cieca» non sa quanto rumore c'è: se toglie molto più del rumore misurato sta
        # cambiando anche toni e colori. La miscela si limita così che il tolto non superi il rumore.
        for k in range(3):
            spread = float(np.std(residual[..., k]))
            if spread > 0 and component_sigma[k] > 0:
                weights[k] = min(weights[k], float(component_sigma[k]) / spread)
        out = original + residual * weights
    else:
        # Il livello di rumore passato alla rete è quello della componente da ripulire: dopo la
        # demosaicizzazione il rumore è correlato tra i canali e pesa soprattutto sulla luminanza.
        luma_sigma = float(component_sigma[0])
        chroma_sigma = float(np.mean(component_sigma[1:]))
        if np.isclose(ls, cs):
            if ls > 0:
                out = rgb_to_opponent(algorithm.denoise_rgb(rgb, luma_sigma * ls, settings.params, progress))
        else:
            if ls > 0:
                luma_run = algorithm.denoise_rgb(rgb, luma_sigma * ls, settings.params, progress)
                out[..., 0] = rgb_to_opponent(luma_run)[..., 0]
            if cs > 0:
                chroma_run = algorithm.denoise_rgb(rgb, chroma_sigma * cs, settings.params, progress)
                out[..., 1:] = rgb_to_opponent(chroma_run)[..., 1:]
    if settings.detail > 0:
        out[..., 0] += settings.detail * (original[..., 0] - out[..., 0])
    return opponent_to_rgb(out).astype(np.float32)
