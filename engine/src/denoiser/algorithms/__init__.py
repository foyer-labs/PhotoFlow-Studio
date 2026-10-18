"""Registro degli algoritmi di denoise disponibili."""

from __future__ import annotations

from .base import Denoiser, Param
from .bilateral import BilateralDenoiser
from .neural import DRUNetDenoiser, NeuralDenoiser, RawNINDDenoiser, RestormerDenoiser, SCUNetDenoiser
from .nlm import NonLocalMeansDenoiser
from .wavelet import WaveletDenoiser

ALGORITHMS: dict[str, Denoiser] = {
    a.key: a
    for a in (
        WaveletDenoiser(), NonLocalMeansDenoiser(), BilateralDenoiser(),
        DRUNetDenoiser(), SCUNetDenoiser(), RestormerDenoiser(), RawNINDDenoiser(),
    )
}


def get_algorithm(key: str) -> Denoiser:
    try:
        return ALGORITHMS[key]
    except KeyError:
        names = ", ".join(ALGORITHMS)
        raise ValueError(f"Algoritmo sconosciuto «{key}». Disponibili: {names}") from None


__all__ = ["ALGORITHMS", "Denoiser", "NeuralDenoiser", "Param", "get_algorithm"]
