"""Algoritmi neurali (DRUNet, SCUNet, Restormer, RawNIND) eseguiti in locale con ONNX Runtime."""

from __future__ import annotations

from typing import Any, Callable

import numpy as np

from .base import Denoiser, Param

ProgressFn = Callable[[float, str], None]

_OVERLAP = 32


class NeuralDenoiser(Denoiser):
    """Lavora su tutti e tre i canali insieme (``joint``) invece che su un canale alla volta."""

    joint = True
    model_key = ""
    margin = 64

    @property
    def spec(self):
        from ..neural.models import MODELS

        return MODELS[self.model_key]

    def is_available(self) -> bool:
        from ..neural.models import is_downloaded

        return is_downloaded(self.spec)

    @property
    def blind(self) -> bool:
        return not self.spec.noise_level_input

    def denoise(self, channel: np.ndarray, sigma: float, params: dict[str, Any]) -> np.ndarray:
        raise TypeError("Gli algoritmi neurali elaborano l'immagine a colori intera (denoise_rgb)")

    def denoise_rgb(
        self, rgb: np.ndarray, sigma: float, params: dict[str, Any], progress: ProgressFn | None = None
    ) -> np.ndarray:
        """``rgb``: (H, W, 3) con valori circa in [0, 1]; ``sigma``: rumore in quelle unità."""
        from ..neural.models import ensure_onnx
        from ..neural.runtime import get_session
        from ..neural.tiling import adaptive_tile, run_tiled

        p = self.resolve(params)
        tile = int(p["tile"])
        session = get_session(ensure_onnx(self.spec, tile, progress), p["device"])
        chw = np.ascontiguousarray(rgb.transpose(2, 0, 1), dtype=np.float32)
        if self.spec.noise_level_input:
            level = np.float32(max(sigma, 0.0))

            def run(t: np.ndarray) -> np.ndarray:
                noise_map = np.full((1, *t.shape[1:]), level, dtype=np.float32)
                return session(np.concatenate([t, noise_map])[None])[0]
        else:

            def run(t: np.ndarray) -> np.ndarray:
                return session(t[None])[0]

        if self.spec.noise_level_input and not session.on_gpu:
            # Sul processore DRUNet accetta qualsiasi misura (multipla di 8): tasselli adattati alla foto,
            # così non si ricalcolano pixel inutili (fino a metà del lavoro con tasselli fissi). Sulla
            # scheda video no: con DirectML ogni misura nuova prepara nuove risorse sulla GPU, che si
            # accumulano da un'anteprima all'altra; lì si usa sempre la stessa misura.
            m = self.spec.multiple
            shape = (adaptive_tile(chw.shape[1], tile, _OVERLAP, m), adaptive_tile(chw.shape[2], tile, _OVERLAP, m))
            out = run_tiled(run, chw, shape, _OVERLAP, progress, label=self.label)
        else:
            # Tasselli quadrati di misura fissa (obbligatori per SCUNet e Restormer, il cui grafo dipende
            # dalla misura). SCUNet: sovrapposizione 16, a parità di qualità e con meno tasselli.
            out = run_tiled(run, chw, tile, self.spec.overlap, progress, label=self.label)
        return np.ascontiguousarray(out.transpose(1, 2, 0))


_DEVICE = Param(
    "device", "Calcolo su", "choice", "auto",
    choices={"auto": "Scheda video se disponibile (consigliato)", "cpu": "Solo processore (CPU)"},
    tooltip="Con una scheda video la rete è molte volte più veloce. «Solo processore» serve solo se la "
    "scheda video dà problemi.",
)

_TILE_TIP = (
    "La foto viene elaborata a pezzi quadrati di questa misura. Non cambia il risultato: incide solo su "
    "velocità e memoria. Se compare un errore di memoria, scegli un valore più piccolo."
)


class DRUNetDenoiser(NeuralDenoiser):
    key = "drunet"
    model_key = "drunet"
    label = "DRUNet (IA)"
    summary = "qualità massima, lento senza scheda video"
    description = (
        "Intelligenza artificiale che usa il rumore misurato in questa foto: in genere dà la qualità "
        "migliore, e i cursori funzionano come per gli altri metodi. Senza scheda video compatibile usa "
        "il processore ed è lenta (anche alcuni minuti per foto)."
    )
    params = (
        _DEVICE,
        Param(
            "tile", "Blocchi di calcolo", "choice", "512",
            choices={"256": "256 px (poca memoria)", "512": "512 px (consigliato)", "1024": "1024 px (veloce)"},
            tooltip=_TILE_TIP,
        ),
    )


class SCUNetDenoiser(NeuralDenoiser):
    key = "scunet"
    model_key = "scunet"
    label = "SCUNet (IA)"
    summary = "per JPG e foto da smartphone"
    description = (
        "Intelligenza artificiale addestrata su foto reali di fotocamere e smartphone: stima il rumore da "
        "sola. Qui «Grana» e «Macchie di colore» decidono quanto del risultato usare (1 = tutto, "
        "0 = foto originale)."
    )
    params = (
        _DEVICE,
        Param(
            "tile", "Blocchi di calcolo", "choice", "256",
            choices={"256": "256 px (consigliato)", "512": "512 px (più memoria)"},
            tooltip=_TILE_TIP,
        ),
    )


class RestormerDenoiser(NeuralDenoiser):
    key = "restormer"
    model_key = "restormer"
    label = "Restormer (IA)"
    summary = "molto pulito, lento"
    description = (
        "Intelligenza artificiale addestrata su rumore reale: stima il rumore da sola e dà un risultato "
        "molto pulito, a volte un po' morbido sui dettagli finissimi. È più lenta di DRUNet: conviene "
        "con una scheda video. «Grana» e «Macchie di colore» decidono quanto del risultato usare."
    )
    params = (
        _DEVICE,
        Param(
            "tile", "Blocchi di calcolo", "choice", "256",
            choices={"256": "256 px (consigliato)", "512": "512 px (più memoria)"},
            tooltip=_TILE_TIP,
        ),
    )


#: RawNIND lavora su tasselli fissi di 512 (dati del sensore impacchettati, o RGB lineare).
_RAWNIND_TILE = 512
#: Da RGB lineare (primari sRGB) a RGB lineare Rec. 2020, lo spazio del modello «lineare».
_SRGB_TO_REC2020 = np.array(
    [[0.6274040, 0.3292820, 0.0433136], [0.0690970, 0.9195400, 0.0113612], [0.0163916, 0.0880132, 0.8955950]],
    dtype=np.float32,
)


class RawNINDDenoiser(NeuralDenoiser):
    """Rete di Brummer e De Vleeschouwer (UtNet2) addestrata su coppie reali di RAW rumorosi e puliti.

    Due modelli nello stesso pacchetto: «bayer» riceve i quattro piani del sensore (RGGB, nero = 0,
    bianco = 1, senza bilanciamento del bianco) e restituisce l'immagine demosaicizzata a piena
    risoluzione; «lineare» riceve RGB lineare Rec. 2020. Entrambi stimano il rumore da soli e
    restituiscono l'immagine a una scala propria, riportata a quella dell'ingresso («match gain»).
    """

    key = "rawnind"
    model_key = "rawnind"
    label = "RawNIND (IA)"
    summary = "dettaglio naturale, per i RAW"
    description = (
        "Intelligenza artificiale addestrata su foto vere di reflex e mirrorless, scattate a ISO alti e "
        "bassi: lavora direttamente sui dati del sensore, conserva molto dettaglio e un aspetto "
        "naturale ed è veloce anche senza scheda video. Pensata per i RAW. «Grana» e «Macchie di "
        "colore» decidono quanto del risultato usare."
    )
    params = (_DEVICE,)
    #: Riconosciuto da pipeline e livello 3: non passa dalla trasformata stabilizzante.
    raw_model = True

    def _session(self, variant: str, params: dict[str, Any], progress: ProgressFn | None):
        from ..neural.models import ensure_onnx
        from ..neural.runtime import get_session

        p = self.resolve(params)
        return get_session(ensure_onnx(self.spec, _RAWNIND_TILE, progress, variant), p["device"])

    def denoise_mosaic(
        self, packed: np.ndarray, params: dict[str, Any], progress: ProgressFn | None = None
    ) -> np.ndarray:
        """``packed``: (4, h, w) piani R, G (destra), G (sotto), B di un mosaico RGGB in [0, 1].

        Restituisce gli stessi quattro piani ripuliti: l'immagine a piena risoluzione prodotta dalla
        rete viene ricampionata sui fotositi (ogni fotosito riceve il suo colore). Così lo sviluppo
        (demosaicizzazione scelta, bilanciamento, DNG dei dati del sensore) resta quello dell'app.
        """
        session = self._session("bayer", params, progress)

        def run(t: np.ndarray) -> np.ndarray:
            # La rete vede i dati in [0, 1]: la scala si misura su ciò che ha ricevuto, così il taglio
            # dei valori sotto il nero non sposta le luci.
            t = np.clip(t, 0.0, 1.0)
            out = session(t[None])[0]  # (3, 2h, 2w)
            remosaic = np.stack([out[0, 0::2, 0::2], out[1, 0::2, 1::2], out[1, 1::2, 0::2], out[2, 1::2, 1::2]])
            return remosaic * _gain(t, remosaic, _BAYER_SCALE, groups=((0,), (1, 2), (3,)))

        return _tiled_reflect(run, packed, progress, self.label)

    def denoise_linear(
        self, rgb: np.ndarray, params: dict[str, Any], progress: ProgressFn | None = None
    ) -> np.ndarray:
        """``rgb``: (H, W, 3) RGB lineare (primari sRGB, bianco = 1). Restituisce lo stesso, ripulito."""
        session = self._session("linear", params, progress)

        def run(t: np.ndarray) -> np.ndarray:
            out = session(t[None])[0]
            return out * _gain(t, out, _LINEAR_SCALE)

        rec2020 = (rgb @ _SRGB_TO_REC2020.T).transpose(2, 0, 1)
        out = _tiled_reflect(run, rec2020, progress, self.label)
        return (out.transpose(1, 2, 0) @ np.linalg.inv(_SRGB_TO_REC2020).T.astype(np.float32)).astype(np.float32)


def _tiled_reflect(run, image: np.ndarray, progress: ProgressFn | None, label: str) -> np.ndarray:
    """Tasselli fissi di RawNIND. Un'immagine più piccola di un tassello viene prima completata a
    specchio: ripetere la riga di bordo (come farebbe la suddivisione in tasselli) darebbe alla rete,
    e al calcolo della scala, strisce che non esistono nella foto."""
    from ..neural.tiling import run_tiled

    c, h, w = image.shape
    need = _RAWNIND_TILE - 2 * _OVERLAP
    pad_y, pad_x = max(0, need - h), max(0, need - w)
    padded = np.pad(image, ((0, 0), (0, pad_y), (0, pad_x)), mode="reflect") if pad_y or pad_x else image
    out = run_tiled(run, np.ascontiguousarray(padded, dtype=np.float32), _RAWNIND_TILE, _OVERLAP, progress, label=label)
    return out[:, :h, :w]


#: Scala propria delle uscite (misurata: stabile entro ±0,5% tra zone e foto diverse; il modello lineare
#: restituisce valori negativi). Il fattore vero si misura su ogni tassello; questi valori servono da
#: riferimento nelle zone quasi nere, dove il rapporto tra le medie non è affidabile.
_BAYER_SCALE = 1 / 1.0095e6
_LINEAR_SCALE = -1 / 3.172e9


def _gain(inp: np.ndarray, out: np.ndarray, reference: float, groups=((0,), (1,), (2,))) -> np.ndarray:
    """Fattori per canale che riportano l'uscita della rete alla luminosità media dell'ingresso
    («match gain»); forma (C, 1, 1).

    La media dei dati rumorosi è una stima senza distorsione di quella vera, quindi un denoise non
    deve spostarla: un fattore comune riporta la scala propria della rete e una piccola correzione per
    colore (al più ±5%) evita leggere dominanti. ``groups``: canali dello stesso colore, corretti
    insieme (i due verdi del sensore, altrimenti comparirebbe un disegno a labirinto).
    """
    mean_in = inp.reshape(inp.shape[0], -1).mean(axis=1, dtype=np.float64)
    mean_out = out.reshape(out.shape[0], -1).mean(axis=1, dtype=np.float64)
    gains = np.full(inp.shape[0], reference, dtype=np.float64)
    total_in, total_out = float(mean_in.mean()), float(mean_out.mean())
    if total_in >= 0.005 and total_out != 0.0:
        low, high = sorted((reference * 0.97, reference * 1.03))
        gains[:] = min(max(total_in / total_out, low), high)
        for group in groups:
            g_in, g_out = float(mean_in[list(group)].mean()), float(mean_out[list(group)].mean()) * gains[group[0]]
            if g_in >= 0.005 and g_out > 0:
                gains[list(group)] *= min(max(g_in / g_out, 0.95), 1.05)
    return gains.astype(np.float32)[:, None, None]
