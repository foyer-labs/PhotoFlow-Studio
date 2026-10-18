"""Filtro bilaterale: sfoca solo tra pixel vicini e di valore simile, preservando i bordi."""

from __future__ import annotations

from typing import Any

import cv2
import numpy as np

from .base import Denoiser, Param

# Rapporto tra sigma del filtro sui valori e sigma del rumore, calibrato su immagini di prova.
_RANGE_PER_SIGMA = 2.0


class BilateralDenoiser(Denoiser):
    key = "bilateral"
    label = "Bilaterale"
    summary = "velocissimo, qualità base"
    description = (
        "Media dei pixel vicini con valori simili. Molto veloce e semplice, "
        "ma a forze alte produce un effetto «acquerello»."
    )
    params = (
        Param(
            "spatial_sigma", "Raggio", "float", 3.0, 0.5, 15.0, 0.5,
            tooltip="Ampiezza in pixel della zona su cui si fa la media. Più grande = più liscio. Consigliato: 3.",
        ),
    )
    margin = 32

    def denoise(self, channel: np.ndarray, sigma: float, params: dict[str, Any]) -> np.ndarray:
        p = self.resolve(params)
        if sigma <= 0:
            return channel.copy()
        return cv2.bilateralFilter(
            np.ascontiguousarray(channel, dtype=np.float32),
            d=0,
            sigmaColor=float(sigma * _RANGE_PER_SIGMA),
            sigmaSpace=float(p["spatial_sigma"]),
            borderType=cv2.BORDER_REFLECT,
        )
