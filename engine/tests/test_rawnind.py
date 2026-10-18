"""RawNIND: integrazione nella pipeline con modelli finti (stessa interfaccia dei veri).

Il modello «bayer» finto ricostruisce l'immagine a piena risoluzione mettendo in ogni fotosito il suo
valore (R in alto a sinistra, G accanto e sotto, B in basso a destra) e la moltiplica per una scala
arbitraria, come fa la rete vera; quello «lineare» restituisce l'ingresso con scala negativa. Se
geometria, ricampionamento sui fotositi e «match gain» sono giusti, il risultato coincide con la foto
senza denoise.
"""

from __future__ import annotations

import zipfile

import numpy as np
import onnx
import pytest
from onnx import TensorProto, helper, numpy_helper

from denoiser.io import load_image
from denoiser.neural import models
from denoiser.pipeline import analyze, denoise_region
from denoiser.settings import Settings
from synthetic import clean_display, clean_linear, write_bayer_dng


def _bayer_model(scale: float) -> onnx.ModelProto:
    # DepthToSpace (CRD): uscita[c, 2h+i, 2w+j] = ingresso[c*4 + i*2 + j, h, w]
    order = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 3, 3]
    nodes = [
        helper.make_node("Gather", ["x", "idx"], ["g"], axis=1),
        helper.make_node("DepthToSpace", ["g"], ["d"], blocksize=2, mode="CRD"),
        helper.make_node("Mul", ["d", "s"], ["y"]),
    ]
    init = [numpy_helper.from_array(np.array(order, np.int64), "idx"),
            numpy_helper.from_array(np.array(scale, np.float32), "s")]
    graph = helper.make_graph(nodes, "bayer", [helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 4, 512, 512])],
                              [helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 3, 1024, 1024])], init)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8
    return model


def _linear_model(scale: float) -> onnx.ModelProto:
    nodes = [helper.make_node("Mul", ["x", "s"], ["y"])]
    graph = helper.make_graph(nodes, "linear", [helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 3, 512, 512])],
                              [helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 3, 512, 512])],
                              [numpy_helper.from_array(np.array(scale, np.float32), "s")])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8
    return model


@pytest.fixture
def fake_rawnind(tmp_path, monkeypatch):
    folder = tmp_path / "modelli"
    folder.mkdir()
    monkeypatch.setenv("DENOISER_MODELS_DIR", str(folder))
    spec = models.MODELS["rawnind"]
    with zipfile.ZipFile(models.weights_path(spec), "w") as zf:
        zf.writestr("rawdenoise-nind/config.json", "{}")
        zf.writestr("rawdenoise-nind/model_bayer.onnx", _bayer_model(1.0095e6).SerializeToString())
        zf.writestr("rawdenoise-nind/model_linear.onnx", _linear_model(-3.172e9).SerializeToString())
    return folder


def _settings(stage: str) -> Settings:
    s = Settings()
    s.denoise.algorithm = "rawnind"
    s.denoise.raw_stage = stage
    s.denoise.params = {"device": "cpu"}
    return s


@pytest.mark.parametrize("stage", ["sensor", "rgb"])
def test_rawnind_identita_conserva_la_foto(tmp_path, fake_rawnind, stage):
    image = load_image(write_bayer_dng(tmp_path / "foto", clean_linear(size=300), a=2e-4, b=2e-6, seed=4,
                                       wb=(2.0, 1.0, 1.5)))
    analysis = analyze(image)
    before = denoise_region(image, analysis, Settings(), None, False)
    after = denoise_region(image, analysis, _settings(stage))
    # Solo il taglio dei valori sotto il nero (ingresso della rete in [0, 1]) e la scala per tassello
    # possono spostare qualcosa.
    assert np.abs(after - before).mean() < 2e-3
    assert np.abs(after - before).max() < 0.05
    assert (fake_rawnind / "rawnind_bayer.onnx").is_file() == (stage == "sensor")
    assert (fake_rawnind / "rawnind_linear.onnx").is_file() == (stage == "rgb")


def test_rawnind_su_jpg(tmp_path, fake_rawnind):
    from PIL import Image

    src = tmp_path / "foto.png"
    Image.fromarray((clean_display(200) * 255).astype(np.uint8)).save(src)
    image = load_image(src)
    analysis = analyze(image)
    after = denoise_region(image, analysis, _settings("rgb"))
    assert np.abs(after - image.pixels).max() < 0.02


def test_rawnind_forza_zero_non_cambia_nulla(tmp_path, fake_rawnind):
    image = load_image(write_bayer_dng(tmp_path / "foto", clean_linear(size=256), a=2e-3, b=2e-5, seed=1))
    analysis = analyze(image)
    s = _settings("sensor")
    s.denoise.luma_strength = s.denoise.chroma_strength = 0.0
    s.denoise.hot_pixel_threshold = 0.0
    assert np.array_equal(denoise_region(image, analysis, s), denoise_region(image, analysis, Settings(), None, False))


_WEIGHTS = __import__("os").environ.get("DENOISER_TEST_WEIGHTS")


@pytest.mark.skipif(not _WEIGHTS, reason="pesi reali non disponibili")
@pytest.mark.parametrize("stage,limit", [("sensor", 0.25), ("rgb", 0.75)])
def test_rawnind_vero_toglie_il_rumore(tmp_path, monkeypatch, stage, limit):
    """Con i pesi veri, su una zona uniforme il rumore deve calare senza spostare i toni (molto sui dati
    del sensore; meno dopo lo sviluppo, dove il rumore è già correlato dalla demosaicizzazione)."""
    monkeypatch.setenv("DENOISER_MODELS_DIR", _WEIGHTS)
    flat = np.full((256, 256, 3), 0.15, dtype=np.float32)
    image = load_image(write_bayer_dng(tmp_path / "piatta", flat, a=8e-4, b=4e-6, seed=2, wb=(2.1, 1.0, 1.4)))
    analysis = analyze(image)
    before = denoise_region(image, analysis, Settings(), None, False)[32:-32, 32:-32]
    after = denoise_region(image, analysis, _settings(stage))[32:-32, 32:-32]
    assert after.std(axis=(0, 1)).max() < limit * before.std(axis=(0, 1)).min()
    assert np.allclose(after.mean(axis=(0, 1)), before.mean(axis=(0, 1)), rtol=0.01)
