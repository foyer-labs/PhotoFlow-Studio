"""Sviluppo della foto: bilanciamento del bianco, esposizione, toni, curva e saturazione.

Tutto avviene dopo la riduzione del rumore e dopo lo sviluppo di LibRaw (che usa sempre il
bilanciamento dello scatto):

- il **bilanciamento del bianco** è una correzione esatta nello spazio colore della fotocamera,
  ``inv(C) · diag(r) · C`` con ``C`` = matrice sRGB lineare → fotocamera e ``r`` il rapporto tra i
  moltiplicatori scelti e quelli dello sviluppo: equivale a risviluppare il RAW, ma è istantaneo;
- **esposizione, alte luci, ombre, contrasto, luminosità e curva** sono una funzione dei toni uguale
  per ogni pixel (niente effetti locali): un'anteprima calcolata a zone o a strisce è identica alla
  foto intera. Si applica canale per canale con una tabella precalcolata;
- la **saturazione** si regola nella luce lineare, attorno alla luminanza, prima dei toni.

I cursori vanno da −100 a +100 (0 = nessun effetto), come in Lightroom, così le regolazioni si
possono scrivere come istruzioni di Camera Raw nei DNG.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .color import srgb_decode, srgb_encode

# Matrici sRGB lineare (D65) <-> XYZ.
SRGB_FROM_XYZ = np.array(
    [[3.2404542, -1.5371385, -0.4985314], [-0.9692660, 1.8760108, 0.0415560], [0.0556434, -0.2040259, 1.0572252]]
)
XYZ_FROM_SRGB = np.linalg.inv(SRGB_FROM_XYZ)
_LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)

#: Temperatura e tinta «come scattato» delle foto già sviluppate (JPG, PNG, TIFF): luce del giorno.
DISPLAY_TEMPERATURE = 6500.0
TEMPERATURE_RANGE = (2000.0, 15000.0)
TINT_RANGE = (-150.0, 150.0)
#: Unità della tinta: 1 = 1/3000 di distanza dal luogo di Planck nel diagramma uv (come Lightroom).
_TINT_SCALE = 3000.0
#: Grigio medio (18%) codificato in sRGB: perno del contrasto.
_MID_GREY = float(srgb_encode(np.float32(0.18)))
#: Voci delle tabelle: con la spaziatura quadratica il passo resta sotto 1/30000 anche nelle ombre.
_LUT_SIZE = 65536
#: Sopra questo valore un canale è considerato saturato (bruciato) dallo sviluppo.
_CLIP = 0.98


# ====================================================================== temperatura del colore


def _planck_uv(t: np.ndarray | float) -> tuple[np.ndarray, np.ndarray]:
    """Luogo di Planck nel diagramma CIE 1960 uv (approssimazione di Krystek, 1000–15000 K)."""
    t = np.asarray(t, dtype=float)
    u = (0.860117757 + 1.54118254e-4 * t + 1.28641212e-7 * t**2) / (1 + 8.42420235e-4 * t + 7.08145163e-7 * t**2)
    v = (0.317398726 + 4.22806245e-5 * t + 4.20481691e-8 * t**2) / (1 - 2.89741816e-5 * t + 1.61456053e-7 * t**2)
    return u, v


def _normal_uv(t: float) -> tuple[float, float]:
    """Direzione perpendicolare al luogo di Planck verso il verde (v crescente)."""
    u0, v0 = _planck_uv(t - 1.0)
    u1, v1 = _planck_uv(t + 1.0)
    du, dv = float(u1 - u0), float(v1 - v0)
    n = np.hypot(du, dv)
    nu, nv = -dv / n, du / n
    return (nu, nv) if nv > 0 else (-nu, -nv)


def xyz_from_temperature(temperature: float, tint: float = 0.0) -> np.ndarray:
    """Bianco (XYZ, Y = 1) di una luce con questa temperatura (K) e tinta (+ = luce più verde)."""
    t = float(np.clip(temperature, *TEMPERATURE_RANGE))
    u, v = (float(x) for x in _planck_uv(t))
    nu, nv = _normal_uv(t)
    d = float(np.clip(tint, *TINT_RANGE)) / _TINT_SCALE
    u, v = u + nu * d, v + nv * d
    x, y = 3 * u / (2 * u - 8 * v + 4), 2 * v / (2 * u - 8 * v + 4)
    return np.array([x / y, 1.0, (1 - x - y) / y])


def multipliers_for(temperature: float, tint: float, cam_from_xyz: np.ndarray) -> np.ndarray:
    """Moltiplicatori del bilanciamento (verde = 1) che rendono neutra una luce temperatura/tinta."""
    neutral = cam_from_xyz @ xyz_from_temperature(temperature, tint)
    k = 1.0 / np.maximum(neutral, 1e-9)
    return k / k[1]


def temperature_from_multipliers(multipliers, cam_from_xyz: np.ndarray) -> tuple[float, float]:
    """Temperatura (K) e tinta corrispondenti a dei moltiplicatori del bilanciamento."""
    neutral = 1.0 / np.maximum(np.asarray(multipliers, dtype=float)[:3], 1e-9)
    xyz = np.linalg.solve(cam_from_xyz, neutral)
    if xyz[1] <= 0 or xyz.sum() <= 0:
        return 5500.0, 0.0
    x, y = xyz[0] / xyz.sum(), xyz[1] / xyz.sum()
    u, v = 4 * x / (-2 * x + 12 * y + 3), 6 * y / (-2 * x + 12 * y + 3)
    # Il punto più vicino sul luogo di Planck, cercato in mired (uniformi per l'occhio) e poi raffinato.
    mired = np.linspace(1e6 / TEMPERATURE_RANGE[1], 1e6 / TEMPERATURE_RANGE[0], 2000)
    pu, pv = _planck_uv(1e6 / mired)
    i = int(np.argmin((pu - u) ** 2 + (pv - v) ** 2))
    lo, hi = mired[max(i - 1, 0)], mired[min(i + 1, len(mired) - 1)]
    for _ in range(40):
        a, b = lo + (hi - lo) / 3, hi - (hi - lo) / 3
        da = np.hypot(*(np.subtract(_planck_uv(1e6 / a), (u, v))))
        db = np.hypot(*(np.subtract(_planck_uv(1e6 / b), (u, v))))
        lo, hi = (lo, b) if da < db else (a, hi)
    t = 1e6 / ((lo + hi) / 2)
    pu, pv = (float(z) for z in _planck_uv(t))
    nu, nv = _normal_uv(t)
    tint = ((u - pu) * nu + (v - pv) * nv) * _TINT_SCALE
    return float(np.clip(t, *TEMPERATURE_RANGE)), float(np.clip(tint, *TINT_RANGE))


@dataclass
class ColorInfo:
    """Colori della foto, per il bilanciamento del bianco."""

    #: Matrice XYZ -> spazio della fotocamera (per JPG/PNG/TIFF: XYZ -> sRGB lineare).
    cam_from_xyz: np.ndarray
    #: Moltiplicatori usati nello sviluppo (verde = 1).
    base: np.ndarray
    #: Moltiplicatori per la luce del giorno (solo RAW).
    daylight: np.ndarray | None = None
    is_raw: bool = True

    @classmethod
    def for_display(cls) -> "ColorInfo":
        """JPG, PNG e TIFF: già bilanciati, «come scattato» = luce del giorno a 6500 K."""
        return cls(SRGB_FROM_XYZ, multipliers_for(DISPLAY_TEMPERATURE, 0.0, SRGB_FROM_XYZ), None, False)

    @property
    def camera_from_srgb(self) -> np.ndarray:
        return self.cam_from_xyz @ XYZ_FROM_SRGB

    def as_shot(self) -> tuple[float, float]:
        """Temperatura e tinta dello sviluppo di partenza."""
        if not self.is_raw:
            return DISPLAY_TEMPERATURE, 0.0
        return temperature_from_multipliers(self.base, self.cam_from_xyz)

    def gray_world(self, rgb_mean: np.ndarray) -> np.ndarray:
        """Rapporto dei moltiplicatori che rende neutra la media ``rgb_mean`` (bilanciamento automatico)."""
        c = self.camera_from_srgb
        r = (c @ np.ones(3)) / np.maximum(c @ np.asarray(rgb_mean, dtype=float), 1e-9)
        return r / r[1]


def wb_ratio(develop, color: ColorInfo, auto_ratio: np.ndarray | None = None) -> np.ndarray:
    """Rapporto tra i moltiplicatori scelti e quelli dello sviluppo (1, 1, 1 = come scattato)."""
    mode = develop.wb_mode
    base = color.base
    if mode == "temperature":
        target = multipliers_for(develop.temperature, develop.tint, color.cam_from_xyz)
    elif mode == "custom" and color.is_raw:
        target = np.asarray(develop.wb_custom, dtype=float)
    elif mode == "daylight" and color.daylight is not None:
        target = color.daylight
    elif mode == "auto" and auto_ratio is not None:
        return np.asarray(auto_ratio, dtype=float)
    else:
        return np.ones(3)
    r = np.asarray(target, dtype=float)[:3] / np.maximum(base, 1e-9)
    return r / r[1] if r[1] > 0 else np.ones(3)


def wb_matrix(ratio: np.ndarray, color: ColorInfo) -> np.ndarray | None:
    """Matrice 3×3 (su RGB lineare) del bilanciamento; None se non cambia nulla."""
    ratio = np.asarray(ratio, dtype=float)
    if np.allclose(ratio, 1.0, atol=1e-6):
        return None
    c = color.camera_from_srgb
    return np.linalg.inv(c) @ np.diag(ratio) @ c


def target_multipliers(develop, color: ColorInfo, auto_ratio: np.ndarray | None = None) -> np.ndarray:
    """Moltiplicatori finali del bilanciamento (verde = 1), es. per l'AsShotNeutral di un DNG."""
    m = color.base * wb_ratio(develop, color, auto_ratio)
    return m / m[1]


# ====================================================================== toni


def _smoothstep(e0: float, e1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def monotone_curve(points, x: np.ndarray) -> np.ndarray:
    """Curva dei toni monotona (Fritsch–Carlson) che passa per ``points`` [(x, y), …] in [0, 1]."""
    pts = sorted((float(px), float(py)) for px, py in points)
    xs = np.array([p[0] for p in pts])
    ys = np.array([p[1] for p in pts])
    if len(xs) < 2:
        return np.asarray(x, dtype=float)
    keep = np.concatenate([[True], np.diff(xs) > 1e-6])
    xs, ys = xs[keep], ys[keep]
    if len(xs) == 2:
        return np.interp(x, xs, ys)
    h = np.diff(xs)
    delta = np.diff(ys) / h
    m = np.empty_like(xs)
    m[0], m[-1] = delta[0], delta[-1]
    m[1:-1] = np.where(delta[:-1] * delta[1:] > 0, (delta[:-1] + delta[1:]) / 2, 0.0)
    for k in range(len(delta)):
        if delta[k] == 0:
            m[k] = m[k + 1] = 0.0
            continue
        a, b = m[k] / delta[k], m[k + 1] / delta[k]
        s = a * a + b * b
        if s > 9:
            t = 3 / np.sqrt(s)
            m[k], m[k + 1] = t * a * delta[k], t * b * delta[k]
    xv = np.clip(np.asarray(x, dtype=float), xs[0], xs[-1])
    k = np.clip(np.searchsorted(xs, xv) - 1, 0, len(h) - 1)
    t = (xv - xs[k]) / h[k]
    t2, t3 = t * t, t * t * t
    y = ((2 * t3 - 3 * t2 + 1) * ys[k] + (t3 - 2 * t2 + t) * h[k] * m[k]
         + (-2 * t3 + 3 * t2) * ys[k + 1] + (t3 - t2) * h[k] * m[k + 1])
    return np.clip(y, 0.0, 1.0)


def is_identity_curve(points) -> bool:
    return all(abs(float(px) - float(py)) < 1e-4 for px, py in points)


def tone_is_neutral(tone) -> bool:
    """True se toni, curva e saturazione non cambiano nulla (esposizione esclusa)."""
    return (tone.contrast == 0 and tone.brightness == 0 and tone.highlights == 0 and tone.shadows == 0
            and tone.saturation == 0 and is_identity_curve(tone.curve))


def encoded_tone(e: np.ndarray, tone) -> np.ndarray:
    """Alte luci (in positivo), ombre, contrasto, luminosità e curva sui valori sRGB [0, 1]."""
    e = np.clip(np.asarray(e, dtype=float), 0.0, 1.0)
    h, s = tone.highlights / 100.0, tone.shadows / 100.0
    c, b = tone.contrast / 100.0, tone.brightness / 100.0
    if h > 0:  # schiarisce le alte luci senza superare il bianco
        e = e + h * 0.35 * _smoothstep(0.45, 0.9, e) * (1 - e)
    if s != 0:  # schiarisce (o scurisce) le ombre, nulla sopra i mezzitoni
        x = np.clip(e / 0.55, 0.0, 1.0)
        bump = 27 / 4 * x * (1 - x) ** 2
        e = e + s * (0.16 if s > 0 else 0.07) * bump
    if c != 0:  # curva a S attorno al grigio medio: nero e bianco restano fermi
        g = 2.0 ** (c * 1.2)
        p = _MID_GREY
        lo = p * np.power(np.clip(e / p, 0, 1), g)
        hi = 1 - (1 - p) * np.power(np.clip((1 - e) / (1 - p), 0, 1), g)
        e = np.where(e < p, lo, hi)
    if b != 0:  # mezzitoni più chiari o più scuri
        e = np.power(np.clip(e, 0, 1), 2.0 ** (-b * 0.8))
    if not is_identity_curve(tone.curve):
        e = monotone_curve(tone.curve, e)
    return np.clip(e, 0.0, 1.0)


def _sqrt_index(values: np.ndarray, top: float) -> np.ndarray:
    """Posizione in una tabella a spaziatura quadratica su [0, top] (più voci nelle ombre)."""
    pos = np.sqrt(np.clip(values, 0.0, top) * np.float32(1.0 / top))
    pos *= np.float32(_LUT_SIZE - 1)
    pos += np.float32(0.5)
    return pos.astype(np.uint16)


def _channel_lut(tone, gain: float, headroom: float) -> tuple[float, np.ndarray]:
    """Tabella per canale: luce lineare (prima del guadagno) -> valore sRGB finale."""
    top = max(headroom, 1.0 / max(gain, 1e-9)) * 1.0001
    grid = top * np.linspace(0.0, 1.0, _LUT_SIZE) ** 2
    light = grid * gain
    h = tone.highlights / 100.0
    if h < 0:  # recupero: Reinhard esteso, il punto più luminoso della foto arriva al bianco
        white = max(headroom * gain, 1.0)
        reinhard = light * (1 + light / (white * white)) / (1 + light)
        # Normalizzato in modo che il bianco della foto resti bianco anche a metà effetto.
        light = (1 + h) * light + (-h) * reinhard
    e = srgb_encode(np.clip(light, 0.0, 1.0).astype(np.float32)).astype(float)
    return top, encoded_tone(e, tone).astype(np.float32)



@dataclass
class ToneParams:
    """Quanto serve, oltre alle impostazioni, per sviluppare i pixel di una foto."""

    color: ColorInfo
    #: Guadagno della luminosità automatica (1 per JPG/PNG/TIFF).
    auto_gain: float = 1.0
    #: Valore lineare più alto (99,95° percentile) della foto, prima dell'esposizione.
    headroom: float = 1.0
    #: Rapporto del bilanciamento automatico (grigio medio della foto).
    auto_wb: np.ndarray | None = None


def render_tone(work: np.ndarray, linear: bool, params: ToneParams, develop, tone) -> np.ndarray:
    """Pixel di lavoro (RGB lineare per i RAW, sRGB per gli altri) -> sRGB [0, 1] pronto da salvare."""
    ratio = wb_ratio(develop, params.color, params.auto_wb)
    matrix = wb_matrix(ratio, params.color)
    gain = (params.auto_gain if (linear and tone.auto_brightness) else 1.0) * 2.0 ** tone.exposure_ev
    if not linear and matrix is None and gain == 1.0 and tone_is_neutral(tone):
        return np.clip(work, 0.0, 1.0).astype(np.float32)  # JPG senza regolazioni: identico all'originale
    lin = work.astype(np.float32) if linear else srgb_decode(work)
    if matrix is not None:
        clipped = _smoothstep(_CLIP - 0.08, _CLIP, lin.max(axis=-1))[..., None].astype(np.float32)
        lin = lin @ matrix.T.astype(np.float32)
        # Le zone bruciate restano neutre: con un altro bilanciamento diventerebbero colorate.
        grey = (lin @ _LUMA)[..., None]
        lin = lin + clipped * (grey - lin)
    if tone_is_neutral(tone):
        return srgb_encode(lin * np.float32(gain))
    if tone.saturation != 0:  # nella luce lineare, attorno alla luminanza (il grigio resta grigio)
        y = (lin @ _LUMA)[..., None]
        lin = (lin - y) * np.float32(1 + tone.saturation / 100.0) + y
    top, values = _channel_lut(tone, gain, params.headroom)
    # Una sola lettura in tabella per pixel e canale: pochi decimi di secondo anche su 50 MP.
    return np.take(values, _sqrt_index(lin, top))


def tone_curve_preview(tone, samples: int = 256) -> np.ndarray:
    """Effetto complessivo di contrasto, luminosità, ombre, alte luci e curva su un'immagine già
    esposta: y per x in [0, 1] (per disegnarlo sopra l'istogramma)."""
    x = np.linspace(0.0, 1.0, samples)
    return encoded_tone(x, tone)


def histogram(encoded: np.ndarray, bins: int = 256) -> np.ndarray:
    """Istogramma (4, bins) di rosso, verde, blu e luminanza di un'immagine sRGB [0, 1] o uint8."""
    arr = np.asarray(encoded)
    if arr.dtype != np.uint8:
        arr = (np.clip(arr, 0.0, 1.0) * 255 + 0.5).astype(np.uint8)
    flat = arr.reshape(-1, 3)
    out = np.empty((4, bins), dtype=np.int64)
    scale = bins / 256
    for ch in range(3):
        out[ch] = np.bincount((flat[:, ch] * scale).astype(np.int64), minlength=bins)[:bins]
    luma = (flat.astype(np.float32) @ _LUMA).astype(np.int64)
    out[3] = np.bincount((luma * scale).astype(np.int64), minlength=bins)[:bins]
    return out


# ====================================================================== istruzioni per Camera Raw

_XMP_TEMPLATE = """<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
 <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
  <rdf:Description rdf:about="" xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
   crs:ProcessVersion="11.0" crs:WhiteBalance="As Shot" crs:HasSettings="True"{attributes}>{curve}
  </rdf:Description>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>"""


def camera_raw_xmp(tone) -> bytes | None:
    """Toni come impostazioni di Lightroom/Camera Raw (pacchetto XMP da incorporare in un DNG).

    Il bilanciamento non c'è: va nell'AsShotNeutral del DNG, che leggono tutti i programmi. Nemmeno
    l'esposizione: va nella BaselineExposure. La luminosità, che Lightroom non ha, entra nella curva.
    None se non c'è nulla da scrivere.
    """
    fields = (("Contrast2012", tone.contrast), ("Highlights2012", tone.highlights),
              ("Shadows2012", tone.shadows), ("Saturation", tone.saturation))
    attributes = "".join(f'\n   crs:{name}="{int(round(value)):+d}"' for name, value in fields if round(value))
    curve = ""
    if tone.brightness != 0 or not is_identity_curve(tone.curve):
        xs = sorted({0.0, 1.0, *np.linspace(0.0, 1.0, 9), *(float(x) for x, _ in tone.curve)})
        neutral = type(tone)(curve=tone.curve, brightness=tone.brightness)  # solo luminosità e curva
        ys = encoded_tone(np.array(xs), neutral)
        items = "".join(f"\n      <rdf:li>{round(x * 255)}, {round(y * 255)}</rdf:li>" for x, y in zip(xs, ys))
        attributes += '\n   crs:ToneCurveName2012="Custom"'
        curve = f"\n   <crs:ToneCurvePV2012>\n    <rdf:Seq>{items}\n    </rdf:Seq>\n   </crs:ToneCurvePV2012>"
    if not attributes and not curve:
        return None
    return _XMP_TEMPLATE.format(attributes=attributes, curve=curve).encode("utf-8")


__all__ = [
    "ColorInfo", "ToneParams", "camera_raw_xmp", "histogram", "monotone_curve", "multipliers_for", "render_tone",
    "target_multipliers", "temperature_from_multipliers", "tone_curve_preview", "tone_is_neutral",
    "wb_matrix", "wb_ratio", "xyz_from_temperature",
]
