"""Rimozione dei pixel caldi (punti isolati molto più luminosi dei vicini)."""

from __future__ import annotations

from typing import Callable

import cv2
import numpy as np


def remove_hot_pixels(
    pixels: np.ndarray,
    threshold: float,
    noise_std: Callable[[np.ndarray, int], np.ndarray],
) -> tuple[np.ndarray, int]:
    """Sostituisce con la mediana locale i pixel che superano i vicini di ``threshold`` volte il rumore.

    ``noise_std(livello, canale)`` restituisce la deviazione del rumore attesa a quel livello.
    Dopo la demosaicizzazione un pixel caldo si allarga a una piccola macchia, per questo
    si usa una mediana 5x5. Un pixel caldo nasce da un solo fotosito ed è quindi fortemente
    colorato; una stella o un riflesso è chiaro in tutti i canali e viene conservato.
    """
    out = pixels.copy()
    kernel = np.ones((3, 3), np.uint8)
    excess, medians = [], []
    for c in range(3):
        x = np.ascontiguousarray(pixels[..., c], dtype=np.float32)
        med = cv2.medianBlur(x, 5)
        excess.append((x - med) / np.maximum(noise_std(med, c), 1e-6))  # in multipli del rumore
        medians.append(med)
    spread = [cv2.dilate(e.astype(np.float32), kernel) for e in excess]
    count = 0
    for c in range(3):
        others = np.maximum(*[spread[j] for j in range(3) if j != c])
        hot = (excess[c] > threshold) & (others < np.maximum(threshold / 3.0, 0.5 * excess[c]))
        count += int(hot.sum())
        out[..., c] = np.where(hot, medians[c], pixels[..., c])
    return out, count
