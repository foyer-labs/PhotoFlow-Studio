import numpy as np
import pytest

from denoiser import vst
from denoiser.noise import NoiseModel, estimate_noise_model, estimate_sigma
from synthetic import clean_linear, poisson_gaussian


def test_estimate_sigma_gaussian():
    rng = np.random.default_rng(0)
    flat = np.full((256, 256), 0.5, dtype=np.float32)
    noisy = flat + rng.normal(0, 0.03, flat.shape).astype(np.float32)
    assert estimate_sigma(noisy) == pytest.approx(0.03, rel=0.1)


def test_estimate_noise_model_recovers_parameters():
    a, b = 1e-3, 1e-5
    noisy = poisson_gaussian(clean_linear(), a, b)
    model = estimate_noise_model(noisy)
    # Le texture dell'immagine gonfiano un po' la stima: tolleranza ampia.
    assert np.all(np.abs(model.a / a - 1) < 0.4)
    assert np.all(model.b < 5 * b)


def test_anscombe_stabilizes_variance():
    model = NoiseModel(a=np.full(3, 2e-3), b=np.full(3, 1e-5))
    stds = []
    for level in (0.02, 0.2, 0.8):
        clean = np.full((128, 128, 3), level, dtype=np.float32)
        noisy = poisson_gaussian(clean, 2e-3, 1e-5, seed=int(level * 100))
        stds.append(vst.forward(noisy, model).std())
    assert np.allclose(stds, 1.0, atol=0.1)


def test_anscombe_inverse_is_unbiased():
    model = NoiseModel(a=np.full(3, 2e-3), b=np.full(3, 1e-5))
    for level in (0.01, 0.1, 0.5):
        clean = np.full((256, 256, 3), level, dtype=np.float32)
        noisy = poisson_gaussian(clean, 2e-3, 1e-5, seed=3)
        # Il denoise ideale di una zona uniforme è la media nel dominio stabilizzato.
        stab = vst.forward(noisy, model)
        ideal = np.full_like(stab, stab.mean())
        assert vst.inverse(ideal, model).mean() == pytest.approx(level, rel=0.03)
