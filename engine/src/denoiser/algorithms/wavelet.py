"""Denoise con soglia sui coefficienti wavelet (come in darktable e RawTherapee)."""

from __future__ import annotations

from itertools import product
from typing import Any

import numpy as np
import pywt
from skimage.restoration import denoise_wavelet

from .base import Denoiser, Param, to_unit_range


class WaveletDenoiser(Denoiser):
    key = "wavelet"
    label = "Wavelet"
    summary = "veloce, ottimo sul colore (consigliato)"
    description = (
        "Scompone la foto in dettagli grandi e piccoli e attenua quelli più deboli del rumore. "
        "Veloce, ottimo soprattutto sulle macchie di colore."
    )
    params = (
        Param(
            "wavelet", "Forma della scomposizione", "choice", "sym4",
            choices={
                "sym4": "Symlet 4 (consigliata)",
                "db1": "Haar (squadrata: può creare blocchi)",
                "db2": "Daubechies 2",
                "coif1": "Coiflet 1",
                "bior2.2": "Biortogonale 2.2",
            },
            tooltip="Il tipo di «mattoncino» con cui la foto viene divisa in dettagli grandi e piccoli. "
            "Cambia poco il risultato: lascia Symlet 4 salvo artefatti a blocchi o righe.",
        ),
        Param(
            "method", "Come riconoscere il rumore", "choice", "BayesShrink",
            choices={
                "BayesShrink": "Adattivo: conserva il dettaglio (consigliato)",
                "VisuShrink": "Uniforme: più forte e più liscio",
            },
            tooltip="«Adattivo» (BayesShrink) decide caso per caso quanto togliere a ogni grandezza di dettaglio. "
            "«Uniforme» (VisuShrink) toglie di più ovunque.",
        ),
        Param(
            "mode", "Taglio del rumore", "choice", "soft",
            choices={"soft": "Morbido: naturale (consigliato)", "hard": "Netto: più contrasto, possibili puntini"},
            tooltip="Il taglio morbido dà un risultato più naturale; quello netto conserva più contrasto "
            "ma può lasciare piccoli puntini.",
        ),
        Param(
            "levels", "Grandezza della grana trattata", "int", 4, 0, 8, 1,
            tooltip="Fino a che dimensione di macchia intervenire (0 = automatica). 1–2: solo grana finissima. "
            "4: grana normale (consigliato). 5–8: anche macchie grandi, tipiche degli ISO molto alti; "
            "con valori alti l'anteprima di una zona può differire un po' dalla foto esportata.",
        ),
        Param(
            "cycle_spin", "Passaggi anti-quadrettatura", "int", 1, 0, 1, 1,
            tooltip="Ripete il calcolo spostando di poco la foto e fa la media, per eliminare piccoli artefatti "
            "a blocchi. 0 = più veloce, 1 = qualità migliore (consigliato, circa 4 volte il tempo). "
            "Più passaggi non migliorano il risultato.",
        ),
    )
    margin = 64

    def denoise(self, channel: np.ndarray, sigma: float, params: dict[str, Any]) -> np.ndarray:
        p = self.resolve(params)
        unit, lo, scale = to_unit_range(channel)
        sigma_unit = sigma / scale
        if sigma_unit <= 0:
            return channel.copy()

        dec_len = pywt.Wavelet(p["wavelet"]).dec_len
        levels = p["levels"]
        if not levels:
            # Automatico: come skimage (massimo - 3), ma calcolato sulla foto intera e non sulla zona
            # in anteprima, altrimenti anteprima ed esportazione userebbero livelli diversi.
            full = int(params.get("_full_size") or min(unit.shape))
            levels = max(1, pywt.dwt_max_level(full, dec_len) - 3)
        # Su immagini piccole (es. anteprime) i livelli possibili sono meno.
        levels = max(1, min(levels, pywt.dwt_max_level(min(unit.shape), dec_len)))

        def run(x: np.ndarray) -> np.ndarray:
            return denoise_wavelet(
                x,
                sigma=sigma_unit,
                wavelet=p["wavelet"],
                mode=p["mode"],
                method=p["method"],
                wavelet_levels=levels,
                rescale_sigma=True,
            )

        shifts = p["cycle_spin"]
        if shifts == 0 or min(unit.shape) <= 16 + shifts:
            out = run(unit)
        else:
            pad = 16 + shifts
            padded = np.pad(unit, pad, mode="reflect")
            acc = np.zeros_like(unit, dtype=np.float64)
            for dy, dx in product(range(shifts + 1), repeat=2):
                shifted = np.roll(padded, (dy, dx), axis=(0, 1))
                den = np.roll(run(shifted), (-dy, -dx), axis=(0, 1))
                acc += den[pad:-pad, pad:-pad]
            out = acc / (shifts + 1) ** 2
        return (out * scale + lo).astype(np.float32)
