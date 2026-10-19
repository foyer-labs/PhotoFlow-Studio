"""Rotazione, raddrizzamento e ritaglio (PhotoFlow Studio, M1)."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
import tifffile
from PIL import Image

from denoiser import geometry
from denoiser.server import Engine
from denoiser.settings import GeometrySettings, Settings


def _g(**kw) -> GeometrySettings:
    return GeometrySettings(**kw)


def _gradient(h: int = 60, w: int = 90) -> np.ndarray:
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    return np.dstack([x / w, y / h, (x + y) / (w + h)]).astype(np.float32)


def test_nessuna_geometria_lascia_la_foto_com_e():
    rgb = _gradient()
    assert geometry.is_identity(GeometrySettings())
    assert geometry.apply(rgb, GeometrySettings()) is rgb


@pytest.mark.parametrize("rotation", [90, 180, 270])
def test_rotazioni_di_90_gradi_esatte(rotation):
    rgb = _gradient()
    out = geometry.apply(rgb, _g(rotation=rotation))
    assert np.array_equal(out, np.rot90(rgb, k=-rotation // 90))
    assert geometry.output_size(90, 60, _g(rotation=rotation)) == ((60, 90) if rotation != 180 else (90, 60))


def test_specchio_e_ritaglio():
    rgb = _gradient()
    out = geometry.apply(rgb, _g(flip=True, crop=(0.5, 0.0, 0.5, 0.5)))
    assert out.shape == (30, 45, 3)
    assert np.array_equal(out, rgb[:, ::-1][0:30, 45:90])


def test_raddrizzamento_senza_angoli_vuoti():
    rgb = np.ones((200, 300, 3), np.float32)
    out = geometry.apply(rgb, _g(angle=8.0))
    assert out.shape == (200, 300, 3)
    assert out.min() > 0.99  # nessun bordo nero: l'ingrandimento copre gli angoli
    assert geometry.straighten_scale(300, 200, 8.0) > 1.0


def test_zona_al_100_per_cento_uguale_alla_foto_intera():
    rgb = _gradient(240, 360)
    g = _g(rotation=90, angle=5.0, crop=(0.1, 0.2, 0.6, 0.5))
    full = geometry.apply(rgb, g)
    region = (20, 30, 50, 40)
    box = geometry.source_box(region, 360, 240, g)
    part = geometry.apply_region(rgb[box[1]:box[1] + box[3], box[0]:box[0] + box[2]], box, region, 360, 240, g)
    x, y, w, h = region
    assert part.shape == (h, w, 3)
    assert np.abs(part - full[y:y + h, x:x + w]).max() < 0.02


def test_punto_dell_uscita_torna_alla_foto():
    g = _g(rotation=270, flip=True, angle=-3.0, crop=(0.2, 0.1, 0.5, 0.7))
    m = geometry.forward(400, 300, g)
    sx, sy = 123.0, 77.0
    ox, oy, _ = m @ np.array([sx, sy, 1.0])
    back = geometry.to_source(ox, oy, 400, 300, g)
    assert back == pytest.approx((sx, sy), abs=1e-6)


def test_raddrizzamento_automatico_ritrova_l_orizzonte():
    rgb = np.zeros((400, 600, 3), np.float32)
    rgb[200:] = 0.9  # orizzonte
    rgb[:, 300:303] = 0.9  # un palo verticale
    tilted = geometry.apply(rgb, _g(angle=3.0))
    angle = geometry.level_angle(tilted)
    assert angle == pytest.approx(-3.0, abs=0.6)
    assert geometry.level_angle(np.zeros((100, 100, 3), np.float32)) is None


def test_orientamento_del_dng():
    assert geometry.compose_orientation(1, _g()) == 1
    assert geometry.compose_orientation(6, _g(rotation=90)) == 3
    assert geometry.compose_orientation(1, _g(flip=True)) == 2
    assert geometry.compose_orientation(6, _g(flip=True)) == 5
    assert geometry.compose_orientation(8, _g(rotation=90)) == 1


def test_ritaglio_del_dng_nelle_coordinate_del_sensore():
    # Sensore 400 × 300, fotocamera girata (6 = 90° orari): l'immagine vista è 300 × 400.
    g = _g(crop=(0.0, 0.0, 0.5, 1.0))  # metà sinistra di quella vista
    assert geometry.default_crop(400, 300, 6, g) == (0, 150, 400, 150)
    assert geometry.default_crop(400, 300, 1, _g()) is None
    assert geometry.default_crop(400, 300, 1, _g(angle=2.0, crop=(0.1, 0.1, 0.5, 0.5))) is None


def test_impostazioni_valide_e_no():
    s = Settings.from_dict({"geometry": {"rotation": 90, "angle": 60, "crop": [0.1, 0.2, 0.5, 0.5], "aspect": "4:5"}})
    assert s.geometry.rotation == 90 and s.geometry.angle == 45.0 and s.geometry.crop == (0.1, 0.2, 0.5, 0.5)
    s = Settings.from_dict({"geometry": {"rotation": 45, "crop": [0.5, 0.5, 0.9, 0.9], "aspect": "7:3"}})
    assert s.geometry.rotation == 0 and s.geometry.crop == (0.0, 0.0, 1.0, 1.0) and s.geometry.aspect == "original"


# ---------------------------------------------------------------------- motore


@pytest.fixture()
def photo(tmp_path):
    path = tmp_path / "foto.tif"
    tifffile.imwrite(path, (_gradient(240, 360) * 65535).astype(np.uint16), photometric="rgb")
    return str(path)


def test_anteprima_ruotata_e_ritagliata(tmp_path, photo):
    engine = Engine(tmp_path / "anteprime")
    s = engine.cmd_open(photo)["settings"]
    s["geometry"] = {"rotation": 90, "crop": [0.0, 0.0, 1.0, 0.5]}
    fit = engine.cmd_render(photo, s, max_side=180)
    assert fit["frame"] == [240, 180]
    assert (fit["width"], fit["height"]) == (180, 135)
    assert engine.cmd_frame(photo, s)["frame"] == [240, 180]
    region = engine.cmd_render(photo, s, region=[10, 10, 40, 30])
    assert np.asarray(Image.open(region["before"])).shape == (30, 40, 3)
    assert region["region"] == [10, 10, 40, 30]


def test_esportazione_con_ritaglio(tmp_path, photo):
    engine = Engine(tmp_path / "anteprime")
    s = engine.cmd_open(photo)["settings"]
    s["geometry"] = {"rotation": 270, "crop": [0.25, 0.25, 0.5, 0.5]}
    output = {"format": "png", "bit_depth": 8, "folder": str(tmp_path / "out"), "name_template": "{nome}",
              "copy_metadata": False}
    result = engine.cmd_export([{"path": photo, "settings": s}], output)["results"][0]
    assert result["outcome"] == "ok", result
    assert Image.open(result["output"]).size == (120, 180)
    assert Path(result["output"]).parent == tmp_path / "out"
