"""Geometria della foto: rotazione di 90°, specchio, raddrizzamento e ritaglio (PhotoFlow Studio, M1).

Le trasformazioni si applicano dopo lo sviluppo, sull'immagine pronta da vedere, in quest'ordine:

1. rotazione a passi di 90° in senso orario e specchio orizzontale;
2. raddrizzamento di ``angle`` gradi (positivo = in senso orario) attorno al centro, con l'ingrandimento
   minimo che non lascia angoli vuoti: la cornice resta quella del punto 1;
3. ritaglio ``crop`` = (x, y, larghezza, altezza) in frazioni di quella cornice.

Le coordinate sono continue: il pixel (i, j) copre [i, i+1) × [j, j+1). «Uscita» è l'immagine finale
alla risoluzione piena; le anteprime ridotte usano la stessa geometria calcolata sulla loro dimensione.
"""

from __future__ import annotations

import math

import cv2
import numpy as np

FULL_CROP = (0.0, 0.0, 1.0, 1.0)


def is_identity(g) -> bool:
    return g.rotation % 360 == 0 and not g.flip and abs(g.angle) < 1e-6 and _crop(g) == FULL_CROP


def _crop(g) -> tuple[float, float, float, float]:
    x, y, w, h = (float(v) for v in g.crop)
    x, y = min(max(x, 0.0), 0.99), min(max(y, 0.0), 0.99)
    return (x, y, min(max(w, 0.01), 1.0 - x), min(max(h, 0.01), 1.0 - y))


def frame_size(width: float, height: float, g) -> tuple[float, float]:
    """Cornice dopo rotazione e specchio (prima del ritaglio)."""
    return (height, width) if g.rotation % 180 == 90 else (width, height)


def output_size(width: int, height: int, g) -> tuple[int, int]:
    fw, fh = frame_size(width, height, g)
    _, _, cw, ch = _crop(g)
    return max(1, round(fw * cw)), max(1, round(fh * ch))


def straighten_scale(fw: float, fh: float, angle: float) -> float:
    """Ingrandimento minimo perché la foto ruotata di ``angle`` copra tutta la cornice fw × fh."""
    a = math.radians(abs(angle))
    c, s = math.cos(a), math.sin(a)
    half_w, half_h = fw / 2, fh / 2
    return max((half_w * c + half_h * s) / half_w, (half_w * s + half_h * c) / half_h)


def _t(dx: float, dy: float) -> np.ndarray:
    return np.array([[1, 0, dx], [0, 1, dy], [0, 0, 1]], dtype=np.float64)


def forward(width: float, height: float, g) -> np.ndarray:
    """Matrice 3×3 (coordinate continue) dall'immagine sviluppata di ``width`` × ``height`` all'uscita."""
    w, h = float(width), float(height)
    turns = (g.rotation // 90) % 4
    if turns == 1:    # 90° orari: (x, y) -> (h - y, x)
        m = np.array([[0, -1, h], [1, 0, 0], [0, 0, 1]], dtype=np.float64)
    elif turns == 2:
        m = np.array([[-1, 0, w], [0, -1, h], [0, 0, 1]], dtype=np.float64)
    elif turns == 3:  # 270° orari: (x, y) -> (y, w - x)
        m = np.array([[0, 1, 0], [-1, 0, w], [0, 0, 1]], dtype=np.float64)
    else:
        m = np.eye(3)
    fw, fh = frame_size(w, h, g)
    if g.flip:
        m = np.array([[-1, 0, fw], [0, 1, 0], [0, 0, 1]], dtype=np.float64) @ m
    if abs(g.angle) > 1e-6:
        a = math.radians(g.angle)
        k = straighten_scale(fw, fh, g.angle)
        c, s = math.cos(a) * k, math.sin(a) * k
        rot = np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], dtype=np.float64)
        m = _t(fw / 2, fh / 2) @ rot @ _t(-fw / 2, -fh / 2) @ m
    cx, cy, _, _ = _crop(g)
    return _t(-cx * fw, -cy * fh) @ m


def _warp(pixels: np.ndarray, matrix: np.ndarray, size: tuple[int, int]) -> np.ndarray:
    """``matrix``: coordinate continue dalla sorgente all'uscita; cv2 lavora sui centri dei pixel."""
    centred = _t(-0.5, -0.5) @ matrix @ _t(0.5, 0.5)
    flags = cv2.INTER_LINEAR
    return cv2.warpAffine(np.ascontiguousarray(pixels), centred[:2], size, flags=flags, borderMode=cv2.BORDER_REPLICATE)


def apply(pixels: np.ndarray, g) -> np.ndarray:
    """Tutta l'immagine (RGB o un solo canale), alla sua dimensione."""
    if g is None or is_identity(g):
        return pixels
    h, w = pixels.shape[:2]
    ow, oh = output_size(w, h, g)
    if abs(g.angle) < 1e-6:  # solo rotazioni di 90°, specchio e ritaglio: esatto, senza interpolare
        out = np.rot90(pixels, k=-((g.rotation // 90) % 4))
        if g.flip:
            out = out[:, ::-1]
        fh, fw = out.shape[:2]
        cx, cy, _, _ = _crop(g)
        x0, y0 = min(round(cx * fw), fw - 1), min(round(cy * fh), fh - 1)
        return np.ascontiguousarray(out[y0:y0 + oh, x0:x0 + ow])
    return _warp(pixels, forward(w, h, g), (ow, oh))


def to_source(x: float, y: float, width: int, height: int, g) -> tuple[float, float]:
    """Punto dell'uscita (risoluzione piena) -> punto della foto sviluppata."""
    inverse = np.linalg.inv(forward(width, height, g))
    sx, sy, _ = inverse @ np.array([x, y, 1.0])
    return float(sx), float(sy)


def source_box(region: tuple[int, int, int, int], width: int, height: int, g, margin: int = 2) -> tuple[int, int, int, int]:
    """Rettangolo della foto sviluppata che serve per la zona ``region`` [x, y, w, h] dell'uscita."""
    x, y, w, h = region
    inverse = np.linalg.inv(forward(width, height, g))
    corners = np.array([[x, y, 1], [x + w, y, 1], [x, y + h, 1], [x + w, y + h, 1]], dtype=np.float64)
    points = corners @ inverse.T
    x0 = max(0, int(math.floor(points[:, 0].min())) - margin)
    y0 = max(0, int(math.floor(points[:, 1].min())) - margin)
    x1 = min(width, int(math.ceil(points[:, 0].max())) + margin)
    y1 = min(height, int(math.ceil(points[:, 1].max())) + margin)
    return x0, y0, max(1, x1 - x0), max(1, y1 - y0)


def apply_region(pixels: np.ndarray, box: tuple[int, int, int, int], region: tuple[int, int, int, int],
                 width: int, height: int, g) -> np.ndarray:
    """La zona ``region`` dell'uscita, da ``pixels`` = il rettangolo ``box`` della foto sviluppata."""
    bx, by, _, _ = box
    rx, ry, rw, rh = region
    matrix = _t(-rx, -ry) @ forward(width, height, g) @ _t(bx, by)
    return _warp(pixels, matrix, (rw, rh))


def clamp_region(region, width: int, height: int, g) -> tuple[int, int, int, int]:
    ow, oh = output_size(width, height, g)
    x, y, w, h = (int(v) for v in region)
    x, y = max(0, min(x, ow - 1)), max(0, min(y, oh - 1))
    return x, y, max(1, min(w, ow - x)), max(1, min(h, oh - y))


# ---------------------------------------------------------------------- raddrizzamento automatico


def level_angle(rgb: np.ndarray) -> float | None:
    """Angolo (gradi, stessa convenzione di ``angle``) che rende orizzontali o verticali le linee lunghe
    della foto; None se non ce ne sono abbastanza."""
    u8 = (np.clip(rgb, 0.0, 1.0) * 255).astype(np.uint8) if rgb.dtype != np.uint8 else rgb
    gray = cv2.cvtColor(u8, cv2.COLOR_RGB2GRAY) if u8.ndim == 3 else u8
    side = max(gray.shape)
    if side > 1600:
        scale = 1600 / side
        gray = cv2.resize(gray, (max(1, round(gray.shape[1] * scale)), max(1, round(gray.shape[0] * scale))),
                          interpolation=cv2.INTER_AREA)
    edges = cv2.Canny(cv2.GaussianBlur(gray, (5, 5), 0), 50, 150)
    min_length = max(40, int(0.12 * max(gray.shape)))
    lines = cv2.HoughLinesP(edges, 1, np.pi / 720, threshold=80, minLineLength=min_length, maxLineGap=6)
    if lines is None:
        return None
    angles, weights = [], []
    for x1, y1, x2, y2 in np.asarray(lines).reshape(-1, 4):
        dx, dy = float(x2 - x1), float(y2 - y1)
        length = math.hypot(dx, dy)
        tilt = math.degrees(math.atan2(dy, dx))  # rispetto all'orizzontale, y verso il basso
        tilt = (tilt + 90) % 180 - 90
        if abs(tilt) <= 15:
            deviation = tilt
        elif abs(abs(tilt) - 90) <= 15:
            deviation = tilt - math.copysign(90, tilt)
        else:
            continue
        angles.append(deviation)
        weights.append(length)
    if sum(weights) < 2 * min_length:
        return None
    order = np.argsort(angles)
    cumulative = np.cumsum(np.array(weights)[order])
    median = float(np.array(angles)[order][np.searchsorted(cumulative, cumulative[-1] / 2)])
    # Una linea inclinata di +t (verso il basso a destra) si raddrizza ruotando di −t.
    return round(-median, 2) if abs(median) >= 0.05 else 0.0


# ---------------------------------------------------------------------- orientamento EXIF (DNG)

#: Codice EXIF -> (rotazione oraria dopo lo specchio, specchio).
_EXIF = {1: (0, False), 2: (0, True), 3: (180, False), 4: (180, True),
         5: (270, True), 6: (90, False), 7: (90, True), 8: (270, False)}
_EXIF_CODE = {v: k for k, v in _EXIF.items()}


def compose_orientation(camera_code: int, g) -> int:
    """Orientamento EXIF del file dopo quello della fotocamera e la rotazione/specchio di ``g``.

    Una trasformazione si scrive come «prima specchio, poi rotazione oraria». Quella di ``g`` è «prima
    rotazione, poi specchio», cioè specchio e poi rotazione opposta.
    """
    r1, m1 = _EXIF.get(camera_code, (0, False))
    mu = bool(g.flip)
    ru = (-g.rotation if mu else g.rotation) % 360
    total_r = (ru + (-r1 if mu else r1)) % 360
    return _EXIF_CODE[(total_r, m1 != mu)]


def default_crop(raw_width: int, raw_height: int, camera_code: int, g) -> tuple[int, int, int, int] | None:
    """Ritaglio di ``g`` nelle coordinate dei dati salvati nel DNG (prima dell'orientamento), per i tag
    DefaultCropOrigin/DefaultCropSize. None senza ritaglio o con un raddrizzamento (non esprimibile)."""
    if _crop(g) == FULL_CROP or abs(g.angle) > 1e-6:
        return None
    r, m = _EXIF[compose_orientation(camera_code, g)]
    # Dati salvati -> immagine vista: specchio, poi rotazione oraria di r.
    shown_w, shown_h = (raw_height, raw_width) if r % 180 == 90 else (raw_width, raw_height)
    cx, cy, cw, ch = _crop(g)
    x0, y0, x1, y1 = cx * shown_w, cy * shown_h, (cx + cw) * shown_w, (cy + ch) * shown_h
    corners = [(x0, y0), (x1, y0), (x0, y1), (x1, y1)]

    def back(px: float, py: float) -> tuple[float, float]:
        # Annulla la rotazione oraria di r (sull'immagine vista), poi lo specchio.
        w, h = shown_w, shown_h
        if r == 90:
            px, py = py, w - px
        elif r == 180:
            px, py = w - px, h - py
        elif r == 270:
            px, py = h - py, px
        if m:
            px = raw_width - px
        return px, py

    points = [back(px, py) for px, py in corners]
    xs, ys = [p[0] for p in points], [p[1] for p in points]
    left, top = max(0, int(round(min(xs)))), max(0, int(round(min(ys))))
    right, bottom = min(raw_width, int(round(max(xs)))), min(raw_height, int(round(max(ys))))
    return left, top, max(1, right - left), max(1, bottom - top)
