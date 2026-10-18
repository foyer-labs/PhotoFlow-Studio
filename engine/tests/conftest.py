from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent))

from synthetic import clean_linear, write_bayer_dng  # noqa: E402

# Rumore forte, tipico di ISO molto alti.
NOISE_A = 4e-3
NOISE_B = 4e-5


@pytest.fixture(scope="session")
def dng_pair(tmp_path_factory) -> tuple[Path, Path]:
    """Lo stesso DNG Bayer sintetico, pulito e con rumore Poisson-gaussiano noto."""
    folder = tmp_path_factory.mktemp("dng")
    linear = clean_linear(size=256)
    clean = write_bayer_dng(folder / "clean.dng", linear)
    noisy = write_bayer_dng(folder / "noisy.dng", linear, a=NOISE_A, b=NOISE_B)
    return clean, noisy
