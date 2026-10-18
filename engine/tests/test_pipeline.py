import os
from pathlib import Path

import numpy as np
import pytest
from skimage.metrics import peak_signal_noise_ratio as psnr

from denoiser.algorithms import ALGORITHMS
from denoiser.bayer import display_rect_to_sensor, orient
from denoiser.image import Domain, ImageData, ImageMeta
from denoiser.io import load_image
from denoiser.io.raw import decode_raw
from denoiser.pipeline import analyze, render, uses_sensor_stage
from denoiser.settings import Settings
from synthetic import clean_display

ALGORITHM_KEYS = sorted(k for k, a in ALGORITHMS.items() if not a.joint)
NEURAL_KEYS = sorted(k for k, a in ALGORITHMS.items() if a.joint)
#: RawNIND è addestrata su RAW veri: sulle immagini sintetiche piccole e molto dettagliate ammorbidisce
#: e il PSNR non migliora (vedi docs/come-funziona.md). La sua verifica è in test_rawnind.py.
PSNR_NEURAL_KEYS = [k for k in NEURAL_KEYS if k != "rawnind"]
_WEIGHTS = os.environ.get("DENOISER_TEST_WEIGHTS")


def _settings(algorithm: str, stage: str = "sensor") -> Settings:
    s = Settings()
    s.denoise.algorithm = algorithm
    s.denoise.raw_stage = stage
    s.tone.auto_brightness = False
    return s


@pytest.mark.parametrize("algorithm", ALGORITHM_KEYS)
def test_display_image_denoise_improves_psnr(algorithm):
    clean = clean_display(256)
    rng = np.random.default_rng(0)
    noisy = np.clip(clean + rng.normal(0, 0.05, clean.shape), 0, 1).astype(np.float32)
    image = ImageData(noisy, Domain.DISPLAY, ImageMeta(Path("x.jpg"), "jpeg"))
    out = render(image, analyze(image), _settings(algorithm))
    assert psnr(clean, out, data_range=1) > psnr(clean, noisy, data_range=1) + 3


@pytest.mark.parametrize("stage", ["sensor", "rgb"])
@pytest.mark.parametrize("algorithm", ALGORITHM_KEYS)
def test_raw_denoise_improves_psnr(dng_pair, algorithm, stage):
    clean_path, noisy_path = dng_pair
    clean = decode_raw(clean_path, measure_noise=False)
    noisy = load_image(noisy_path)
    settings = _settings(algorithm, stage)
    reference = render(clean, analyze(clean), settings, denoise=False)
    analysis = analyze(noisy)
    before = psnr(reference, render(noisy, analysis, settings, denoise=False), data_range=1)
    after = psnr(reference, render(noisy, analysis, settings), data_range=1)
    assert after > before + 1.0


def test_sensor_stage_beats_rgb_stage(dng_pair):
    clean_path, noisy_path = dng_pair
    clean = decode_raw(clean_path, measure_noise=False)
    noisy = load_image(noisy_path)
    analysis = analyze(noisy)
    results = {}
    for stage in ("sensor", "rgb"):
        settings = _settings("wavelet", stage)
        reference = render(clean, analyze(clean), settings, denoise=False)
        results[stage] = psnr(reference, render(noisy, analysis, settings), data_range=1)
    assert results["sensor"] > results["rgb"]


def test_noise_twin_measures_sensor_noise(dng_pair):
    from conftest import NOISE_A

    _, noisy_path = dng_pair
    image = load_image(noisy_path)
    assert image.raw_source is not None
    sensor = image.noise_profile.sensor_model
    assert np.all(np.abs(sensor.a / NOISE_A - 1) < 0.25)


@pytest.mark.parametrize("stage", ["sensor", "rgb"])
@pytest.mark.parametrize("algorithm", ["nlm", "bilateral"])
def test_region_matches_full_render(dng_pair, algorithm, stage):
    _, noisy_path = dng_pair
    image = load_image(noisy_path)
    analysis = analyze(image)
    settings = _settings(algorithm, stage)
    full = render(image, analysis, settings)
    region = (40, 30, 100, 80)
    part = render(image, analysis, settings, region=region)
    x, y, w, h = region
    assert part.shape == (h, w, 3)
    assert np.percentile(np.abs(full[y : y + h, x : x + w] - part), 99) < 0.01


def test_disabled_strengths_leave_image_unchanged(dng_pair):
    _, noisy_path = dng_pair
    image = load_image(noisy_path)
    analysis = analyze(image)
    settings = _settings("wavelet")
    settings.denoise.luma_strength = 0
    settings.denoise.chroma_strength = 0
    assert np.array_equal(render(image, analysis, settings), render(image, analysis, settings, denoise=False))


def test_sensor_stage_used_only_for_bayer_raw(dng_pair):
    _, noisy_path = dng_pair
    raw_image = load_image(noisy_path)
    jpeg_like = ImageData(clean_display(64), Domain.DISPLAY, ImageMeta(Path("x.jpg"), "jpeg"))
    assert uses_sensor_stage(raw_image, Settings())
    assert not uses_sensor_stage(jpeg_like, Settings())
    rgb = Settings()
    rgb.denoise.raw_stage = "rgb"
    assert not uses_sensor_stage(raw_image, rgb)


@pytest.mark.parametrize("flip", [0, 3, 5, 6])
def test_display_rect_maps_to_sensor(flip):
    sensor = np.arange(6 * 10).reshape(6, 10)
    shown = orient(sensor, flip)
    rect = (1, 2, 3, 2)  # x, y, w, h nell'immagine orientata
    x, y, w, h = rect
    sx, sy, sw, sh = display_rect_to_sensor(rect, flip, sensor.shape[1], sensor.shape[0])
    expected = shown[y : y + h, x : x + w]
    assert np.array_equal(orient(sensor[sy : sy + sh, sx : sx + sw], flip), expected)


@pytest.mark.skipif(not _WEIGHTS, reason="pesi reali non disponibili")
@pytest.mark.parametrize("stage", ["sensor", "rgb"])
@pytest.mark.parametrize("algorithm", PSNR_NEURAL_KEYS)
def test_neural_raw_denoise(dng_pair, monkeypatch, algorithm, stage):
    monkeypatch.setenv("DENOISER_MODELS_DIR", _WEIGHTS)
    clean_path, noisy_path = dng_pair
    clean = decode_raw(clean_path, measure_noise=False)
    noisy = load_image(noisy_path)
    settings = _settings(algorithm, stage)
    settings.denoise.params = {"tile": "256"}
    reference = render(clean, analyze(clean), settings, denoise=False)
    analysis = analyze(noisy)
    before = psnr(reference, render(noisy, analysis, settings, denoise=False), data_range=1)
    after = psnr(reference, render(noisy, analysis, settings), data_range=1)
    assert after > before + 1.5


def test_neural_algorithm_without_weights_reports_clearly(dng_pair, monkeypatch, tmp_path):
    from denoiser.neural.models import ModelNotAvailableError

    monkeypatch.setenv("DENOISER_MODELS_DIR", str(tmp_path))
    _, noisy_path = dng_pair
    image = load_image(noisy_path)
    with pytest.raises(ModelNotAvailableError, match="modelli scarica drunet"):
        render(image, analyze(image), _settings("drunet"))


@pytest.mark.parametrize("flip", [6, 5, 3])
def test_rotated_raw_region_matches_full(tmp_path, dng_pair, flip):
    """Foto verticali/capovolte: l'anteprima di una zona al livello 3 deve coincidere con la foto intera."""
    import rawpy

    from denoiser.io.dng import XYZ_TO_SRGB, SensorData, write_bayer_dng

    _, noisy_path = dng_pair
    with rawpy.imread(str(noisy_path)) as raw:
        cfa = raw.raw_image_visible[:, :200].copy()  # non quadrata
    sensor = SensorData(
        cfa=cfa, pattern=np.array([[0, 1], [1, 2]], dtype=np.uint8), black=np.full((2, 2), 256.0),
        white=4095, xyz_to_camera=XYZ_TO_SRGB, camera_neutral=np.ones(3), flip=flip,
    )
    image = load_image(write_bayer_dng(tmp_path / "ruotata.dng", sensor))
    expected_shape = (200, 256) if flip in (5, 6) else (256, 200)
    assert (image.height, image.width) == expected_shape
    analysis = analyze(image)
    settings = _settings("nlm", "sensor")
    full = render(image, analysis, settings)
    region = (30, 50, 90, 70)
    x, y, w, h = region
    part = render(image, analysis, settings, region=region)
    assert np.percentile(np.abs(full[y : y + h, x : x + w] - part), 99) < 0.01
