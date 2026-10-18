"""Elaborazione a tasselli: le reti lavorano su pezzi di dimensione fissa, poi ricomposti.

I tasselli si sovrappongono e vengono fusi con pesi che sfumano verso i bordi, così le
giunture non si vedono e la memoria usata resta limitata anche su foto da 24+ MP.
"""

from __future__ import annotations

from typing import Callable

import numpy as np

ProgressFn = Callable[[float, str], None]


def _ramp(size: int, overlap: int) -> np.ndarray:
    ramp = np.ones(size, dtype=np.float32)
    if overlap > 0:
        edge = (np.arange(overlap, dtype=np.float32) + 0.5) / overlap
        ramp[:overlap] = edge
        ramp[-overlap:] = edge[::-1]
    return ramp


def _blend_window(size_y: int, size_x: int, overlap: int) -> np.ndarray:
    return np.outer(_ramp(size_y, overlap), _ramp(size_x, overlap))


def adaptive_tile(length: int, max_tile: int, overlap: int, multiple: int = 8) -> int:
    """Lato del tassello che copre ``length`` con il minimo di pixel ripetuti.

    Con tasselli fissi un'immagine di 600 px richiede due tasselli da 512 (quasi il doppio del
    lavoro); qui si usa il numero minimo di tasselli, tutti della stessa misura e il più piccoli
    possibile. Solo per reti che accettano qualsiasi dimensione (multipla di ``multiple``).
    """
    padded = length + 2 * overlap
    step_max = max(1, max_tile - 2 * overlap)
    n = max(1, -(-(padded - 2 * overlap) // step_max))
    size = -(-(padded + (n - 1) * 2 * overlap) // n)
    return int(-(-size // multiple) * multiple)


def _starts(length: int, tile: int, step: int) -> list[int]:
    if length <= tile:
        return [0]
    starts = list(range(0, length - tile, step))
    starts.append(length - tile)
    return sorted(set(starts))


def run_tiled(
    fn: Callable[[np.ndarray], np.ndarray],
    image: np.ndarray,
    tile: int | tuple[int, int],
    overlap: int,
    progress: ProgressFn | None = None,
    label: str = "Rete neurale",
) -> np.ndarray:
    """Applica ``fn`` (C, th, tw) -> (C', th, tw) a un'immagine (C, H, W); ``tile``: lato o (th, tw)."""
    c, h, w = image.shape
    th, tw = (tile, tile) if isinstance(tile, int) else tile
    # Contorno riflesso: anche i pixel ai bordi hanno contesto, e l'immagine è almeno un tassello.
    pad_y = overlap + max(0, th - (h + 2 * overlap))
    pad_x = overlap + max(0, tw - (w + 2 * overlap))
    mode = "reflect" if min(h, w) > max(pad_y, pad_x) else "edge"
    padded = np.pad(image, ((0, 0), (overlap, pad_y), (overlap, pad_x)), mode=mode)
    ph, pw = padded.shape[1:]
    ys = _starts(ph, th, max(1, th - 2 * overlap))
    xs = _starts(pw, tw, max(1, tw - 2 * overlap))
    weight = _blend_window(th, tw, overlap)
    out: np.ndarray | None = None
    norm = np.zeros((ph, pw), dtype=np.float32)
    total = len(ys) * len(xs)
    done = 0
    for y in ys:
        for x in xs:
            result = fn(np.ascontiguousarray(padded[:, y : y + th, x : x + tw]))
            if out is None:
                out = np.zeros((result.shape[0], ph, pw), dtype=np.float32)
            out[:, y : y + th, x : x + tw] += result * weight
            norm[y : y + th, x : x + tw] += weight
            done += 1
            if progress is not None:
                progress(done / total, f"{label}: tassello {done} di {total}")
    assert out is not None
    out /= np.maximum(norm, 1e-6)
    return out[:, overlap : overlap + h, overlap : overlap + w]
