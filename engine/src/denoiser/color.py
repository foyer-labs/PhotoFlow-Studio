"""Conversioni di colore."""

from __future__ import annotations

import numpy as np

# Trasformazione "opponente" ortonormale: una componente di luminanza e due di crominanza.
# Essendo ortonormale, un rumore indipendente e uguale sui tre canali resta tale
# (stessa deviazione standard) anche nelle tre componenti: utile per dosare il denoise.
_OPPONENT = np.array(
    [
        [1 / np.sqrt(3), 1 / np.sqrt(3), 1 / np.sqrt(3)],
        [1 / np.sqrt(2), 0.0, -1 / np.sqrt(2)],
        [1 / np.sqrt(6), -2 / np.sqrt(6), 1 / np.sqrt(6)],
    ],
    dtype=np.float32,
)
_OPPONENT_INV = _OPPONENT.T.copy()


def rgb_to_opponent(rgb: np.ndarray) -> np.ndarray:
    """RGB (H, W, 3) -> (luminanza, crominanza 1, crominanza 2)."""
    return rgb @ _OPPONENT.T


def opponent_to_rgb(opp: np.ndarray) -> np.ndarray:
    return opp @ _OPPONENT_INV.T


def srgb_encode(linear: np.ndarray) -> np.ndarray:
    """RGB lineare -> sRGB con gamma (valori fuori da [0, 1] vengono limitati)."""
    x = np.clip(linear, 0.0, 1.0)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055).astype(np.float32)


def srgb_decode(encoded: np.ndarray) -> np.ndarray:
    """sRGB con gamma -> RGB lineare."""
    x = np.clip(encoded, 0.0, 1.0)
    return np.where(x <= 0.04045, x / 12.92, np.power((x + 0.055) / 1.055, 2.4)).astype(np.float32)
