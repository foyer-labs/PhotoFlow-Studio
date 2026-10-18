"""Sviluppo: bilanciamento del bianco, toni, curva e saturazione."""

from __future__ import annotations

import json

import numpy as np
import pytest

from denoiser.color import srgb_decode, srgb_encode
from denoiser.develop import (
    SRGB_FROM_XYZ,
    ColorInfo,
    ToneParams,
    encoded_tone,
    histogram,
    monotone_curve,
    multipliers_for,
    render_tone,
    temperature_from_multipliers,
    wb_ratio,
)
from denoiser.io import load_image
from denoiser.pipeline import analyze, render
from denoiser.settings import DevelopSettings, Settings, ToneSettings
from synthetic import clean_display

# Matrice di una fotocamera qualsiasi (XYZ -> camera), non diagonale come quelle vere.
CAMERA = np.array([[0.75, 0.10, -0.12], [-0.45, 1.30, 0.15], [-0.08, 0.20, 0.62]])


def _raw_color() -> ColorInfo:
    return ColorInfo(CAMERA, multipliers_for(5200, 8, CAMERA), multipliers_for(5500, 0, CAMERA), True)


def _tone(**kw) -> ToneSettings:
    t = ToneSettings(auto_brightness=False)
    for k, v in kw.items():
        setattr(t, k, v)
    return t


@pytest.mark.parametrize("temperature, tint", [(2800, 0), (5200, 8), (6500, -20), (9000, 30)])
def test_temperatura_andata_e_ritorno(temperature, tint):
    t, g = temperature_from_multipliers(multipliers_for(temperature, tint, CAMERA), CAMERA)
    assert t == pytest.approx(temperature, rel=0.01)
    assert g == pytest.approx(tint, abs=1.0)


def test_come_scattato_ricava_la_temperatura_dello_scatto():
    t, g = _raw_color().as_shot()
    assert t == pytest.approx(5200, rel=0.01) and g == pytest.approx(8, abs=1.0)


def test_temperatura_piu_alta_scalda_la_foto():
    grey = np.full((4, 4, 3), 0.18, np.float32)
    params = ToneParams(_raw_color())
    tone = _tone()
    out = {}
    for t in (4000, 5200, 8000):
        d = DevelopSettings(wb_mode="temperature", temperature=t, tint=8)
        out[t] = render_tone(grey, True, params, d, tone)[0, 0]
    assert out[8000][0] > out[5200][0] > out[4000][0]  # più rosso
    assert out[8000][2] < out[5200][2] < out[4000][2]  # meno blu
    assert np.allclose(out[5200], srgb_encode(np.float32(0.18)), atol=2e-3)  # = come scattato


def test_come_scattato_non_cambia_nulla_nei_raw():
    rng = np.random.default_rng(0)
    work = rng.uniform(0, 0.6, (32, 32, 3)).astype(np.float32)
    params = ToneParams(_raw_color(), auto_gain=1.7)
    tone = ToneSettings(exposure_ev=0.5)
    out = render_tone(work, True, params, DevelopSettings(), tone)
    assert np.array_equal(out, srgb_encode(work * np.float32(1.7 * 2**0.5)))


def test_jpg_senza_regolazioni_identico():
    work = clean_display(size=48)
    out = render_tone(work, False, ToneParams(ColorInfo.for_display()), DevelopSettings(), ToneSettings())
    assert np.array_equal(out, np.clip(work, 0, 1))


def test_jpg_temperatura_di_partenza_neutra():
    color = ColorInfo.for_display()
    d = DevelopSettings(wb_mode="temperature", temperature=6500, tint=0)
    assert np.allclose(wb_ratio(d, color), 1.0)


def test_bilanciamento_automatico_neutralizza_una_dominante():
    rng = np.random.default_rng(1)
    scene = rng.uniform(0.05, 0.5, (64, 64, 1)).astype(np.float32) * np.ones(3, np.float32)
    cast = scene * np.array([1.3, 1.0, 0.7], np.float32)  # luce calda
    color = ColorInfo(SRGB_FROM_XYZ, np.ones(3))
    ratio = color.gray_world(cast.reshape(-1, 3).mean(axis=0))
    params = ToneParams(color, auto_wb=ratio)
    out = srgb_decode(render_tone(cast, True, params, DevelopSettings(wb_mode="auto"), _tone()))
    mean = out.reshape(-1, 3).mean(axis=0)
    assert mean[0] == pytest.approx(mean[1], rel=0.02) and mean[2] == pytest.approx(mean[1], rel=0.02)


def test_zone_bruciate_restano_neutre():
    white = np.ones((2, 2, 3), np.float32)
    d = DevelopSettings(wb_mode="temperature", temperature=3000, tint=0)
    out = render_tone(white, True, ToneParams(_raw_color()), d, _tone())
    assert np.ptp(out[0, 0]) < 0.02


def test_contrasto_lascia_fermi_nero_grigio_e_bianco():
    grey = float(srgb_encode(np.float32(0.18)))
    x = np.array([0.0, grey, 1.0, 0.25, 0.75])
    y = encoded_tone(x, _tone(contrast=60))
    assert y[:3] == pytest.approx([0.0, grey, 1.0], abs=1e-6)
    assert y[3] < 0.25 and y[4] > 0.75  # ombre più scure, luci più chiare
    y = encoded_tone(x, _tone(contrast=-60))
    assert y[3] > 0.25 and y[4] < 0.75


def test_ombre_e_luminosita():
    x = np.array([0.1, 0.5, 0.9])
    assert encoded_tone(x, _tone(shadows=80))[0] > 0.1
    assert encoded_tone(x, _tone(shadows=80))[2] == pytest.approx(0.9)
    assert encoded_tone(x, _tone(shadows=-80))[0] < 0.1
    assert encoded_tone(x, _tone(brightness=50))[1] > 0.5
    assert encoded_tone(x, _tone(brightness=-50))[1] < 0.5


def test_recupero_alte_luci():
    params = ToneParams(ColorInfo(SRGB_FROM_XYZ, np.ones(3)), headroom=3.0)
    bright = np.array([[[3.0, 2.0, 1.5]]], np.float32)  # oltre il bianco: bruciato senza recupero
    plain = render_tone(bright, True, params, DevelopSettings(), _tone())
    assert np.allclose(plain, 1.0)
    recovered = render_tone(bright, True, params, DevelopSettings(), _tone(highlights=-100))
    assert recovered.max() <= 1.0 and recovered[0, 0, 2] < recovered[0, 0, 1] < recovered[0, 0, 0] <= 1.0
    assert recovered[0, 0, 2] < 0.99
    # In positivo le alte luci si schiariscono senza superare il bianco.
    up = encoded_tone(np.array([0.7, 1.0]), _tone(highlights=100))
    assert up[0] > 0.7 and up[1] == pytest.approx(1.0)


def test_curva_monotona_passa_per_i_punti():
    points = ((0.0, 0.0), (0.25, 0.15), (0.5, 0.55), (0.8, 0.95), (1.0, 1.0))
    x = np.linspace(0, 1, 501)
    y = monotone_curve(points, x)
    assert np.all(np.diff(y) >= -1e-9)
    for px, py in points:
        assert monotone_curve(points, np.array([px]))[0] == pytest.approx(py, abs=1e-9)


def test_saturazione():
    pixel = np.array([[[0.4, 0.2, 0.1]]], np.float32)
    params = ToneParams(ColorInfo(SRGB_FROM_XYZ, np.ones(3)))
    grey = render_tone(pixel, True, params, DevelopSettings(), _tone(saturation=-100))
    assert np.ptp(grey) < 1e-3
    vivid = render_tone(pixel, True, params, DevelopSettings(), _tone(saturation=50))
    plain = render_tone(pixel, True, params, DevelopSettings(), _tone())
    assert np.ptp(vivid) > np.ptp(plain)


def test_strisce_uguali_alla_foto_intera():
    """Nessun effetto locale: l'anteprima a zone coincide con la foto intera."""
    rng = np.random.default_rng(2)
    work = rng.uniform(0, 1.4, (64, 48, 3)).astype(np.float32)
    params = ToneParams(_raw_color(), auto_gain=1.3, headroom=1.4)
    d = DevelopSettings(wb_mode="temperature", temperature=4300, tint=-12)
    tone = _tone(contrast=30, brightness=10, highlights=-50, shadows=40, saturation=20,
                 curve=((0, 0), (0.3, 0.25), (0.7, 0.8), (1, 1)))
    whole = render_tone(work, True, params, d, tone)
    strips = np.concatenate([render_tone(work[i:i + 16], True, params, d, tone) for i in range(0, 64, 16)])
    assert np.array_equal(whole, strips)


def test_istogramma():
    img = np.zeros((10, 10, 3), np.uint8)
    img[:5] = 255
    h = histogram(img)
    assert h.shape == (4, 256)
    assert h[0, 0] == 50 and h[0, 255] == 50 and h[3, 255] == 50


def test_impostazioni_con_curva_da_json():
    s = Settings()
    s.tone.curve = ((0.0, 0.1), (0.5, 0.6), (1.0, 0.9))
    s.develop.wb_mode = "temperature"
    s.develop.temperature = 4100
    back = Settings.from_dict(json.loads(json.dumps(s.to_dict())))
    assert back.tone.curve == s.tone.curve and back.develop.temperature == 4100
    bad = Settings.from_dict({"tone": {"curve": [[0, 0]], "contrast": 500}})
    assert bad.tone.curve == Settings().tone.curve and bad.tone.contrast == 100


def test_raw_vero_bilanciamento_e_toni(dng_pair):
    """DNG sintetico: «come scattato» resta com'era, la temperatura cambia i colori in modo coerente."""
    _clean, noisy = dng_pair
    image = load_image(noisy)
    assert image.meta.color is not None
    analysis = analyze(image)
    s = Settings()
    before = render(image, analysis, s, region=(0, 0, 64, 64), denoise=False)
    s.develop.wb_mode = "temperature"
    s.develop.temperature, s.develop.tint = analysis.color.as_shot()
    same = render(image, analysis, s, region=(0, 0, 64, 64), denoise=False)
    assert np.abs(same - before).max() < 0.01
    s.develop.temperature += 2500
    warmer = render(image, analysis, s, region=(0, 0, 64, 64), denoise=False)
    assert warmer[..., 0].mean() > before[..., 0].mean() and warmer[..., 2].mean() < before[..., 2].mean()
