"""Regressioni per i problemi trovati nella revisione del codice."""

from __future__ import annotations

import numpy as np
from PIL import Image, ImageCms

from denoiser.batch import collect_inputs, export_image, process_file
from denoiser.io import load_image
from denoiser.io.naming import render_name
from denoiser.neural.tiling import adaptive_tile, run_tiled
from denoiser.noise import NoiseModel
from denoiser.pipeline import analyze, denoise_region, render
from denoiser.settings import Settings
from denoiser import vst
from synthetic import clean_linear, write_bayer_dng


def test_settings_tollerano_valori_sbagliati():
    s = Settings.from_dict({
        "denoise": {"luma_strength": "0,8", "algorithm": "inesistente", "params": 5},
        "output": {"bit_depth": "8", "format": "webp", "jpeg_quality": 0},
        "tone": {"auto_brightness": "false"},
        "develop": {"wb_custom": [2, 1]},
    })
    assert s.denoise.luma_strength == 0.8
    assert s.denoise.algorithm == "wavelet" and s.denoise.params == {}
    assert s.output.bit_depth == 8 and s.output.format == "tiff" and s.output.jpeg_quality == 1
    assert s.tone.auto_brightness is False
    assert s.develop.wb_custom == Settings().develop.wb_custom
    assert Settings.from_dict(Settings().to_dict()) == Settings()
    assert Settings.from_dict([1, 2]) == Settings()


def test_nome_con_segnaposto_strano_non_fallisce():
    from denoiser.image import ImageMeta
    from pathlib import Path

    meta = ImageMeta(source=Path("IMG_1.dng"), kind="raw")
    assert render_name("{nome.x}_{0}", meta, "wavelet")


def test_vst_grigio_neutro_uguale_su_tutti_i_canali():
    """Con guadagni diversi per canale un grigio deve restare neutro dopo la trasformata
    (altrimenti i suoi bordi finiscono nel colore e il denoise del colore crea aloni)."""
    model = NoiseModel(a=np.array([4e-3, 1.7e-3, 2.5e-3]), b=np.array([4e-5, 1.7e-5, 2.5e-5]) * 0)
    grey = np.full((1, 1, 3), 0.3, dtype=np.float32)
    stab = vst.forward(grey, model)[0, 0]
    assert np.ptp(stab) / stab.mean() < 0.05  # senza pesi per canale sarebbe ~0,4
    assert np.allclose(vst.inverse(vst.forward(grey, model), model), grey, rtol=0.02)


def test_pixel_caldi_rimossi_ma_stelle_conservate(tmp_path):
    rng = np.random.default_rng(5)
    h = w = 200
    lin = np.full((h, w, 3), 0.01, np.float32)
    yy, xx = np.mgrid[0:h, 0:w]
    stars = []
    for _ in range(12):
        cy, cx = rng.uniform(15, h - 15), rng.uniform(15, w - 15)
        lin += (0.4 * np.exp(-((yy - cy) ** 2 + (xx - cx) ** 2) / (2 * 0.8**2)))[..., None].astype(np.float32)
        stars.append((int(round(cy)), int(round(cx))))
    hots = [(int(rng.integers(10, h - 10)), int(rng.integers(10, w - 10))) for _ in range(12)]
    for y, x in hots:
        lin[y, x, :] = 0.8
    image = load_image(write_bayer_dng(tmp_path / "stelle", lin, a=2e-3, b=2e-5, seed=1))
    analysis = analyze(image)

    def bump(out, y, x):
        return float(out[y - 1 : y + 2, x - 1 : x + 2].max() - np.median(out[y - 6 : y + 7, x - 6 : x + 7]))

    for stage in ("sensor", "rgb"):
        s = Settings()
        s.denoise.luma_strength = s.denoise.chroma_strength = 0.0
        s.denoise.hot_pixel_threshold = 8.0
        s.denoise.raw_stage = stage
        out = denoise_region(image, analysis, s)
        star_kept = np.median([bump(out, y, x) / max(bump(image.pixels, y, x), 1e-6) for y, x in stars])
        hot_left = np.median([bump(out, y, x) / max(bump(image.pixels, y, x), 1e-6) for y, x in hots])
        assert star_kept > 0.85, stage
        assert hot_left < 0.3, stage


def test_profilo_icc_conservato(tmp_path):
    lab = ImageCms.ImageCmsProfile(ImageCms.createProfile("LAB")).tobytes()
    src = tmp_path / "foto.jpg"
    Image.fromarray(np.full((24, 32, 3), 128, np.uint8)).save(src, icc_profile=lab)
    image = load_image(src)
    for fmt in ("jpeg", "png", "tiff"):
        s = Settings()
        s.output.format = fmt
        out = export_image(image, analyze(image), s, tmp_path / f"out.{fmt}")
        with Image.open(out) as im:
            assert im.info.get("icc_profile") == lab, fmt


def test_batch_non_rielabora_la_cartella_di_uscita_e_non_sovrascrive(tmp_path):
    src = tmp_path / "IMG_1.jpg"
    Image.fromarray(np.full((24, 32, 3), 100, np.uint8)).save(src)
    Image.fromarray(np.full((24, 32, 3), 100, np.uint8)).save(tmp_path / "IMG_1.png")
    s = Settings()
    s.output.overwrite = True
    produced: set = set()
    files = collect_inputs([tmp_path], recursive=True, output_folder=s.output.folder)
    results = [process_file(f, s, use_dng_converter=False, taken=produced) for f in files]
    assert all(r.ok for r in results)
    assert len({r.output for r in results}) == 2  # stesso nome di uscita, nessuna sovrascrittura
    again = collect_inputs([tmp_path], recursive=True, output_folder=s.output.folder)
    assert sorted(p.name for p in again) == ["IMG_1.jpg", "IMG_1.png"]


def test_mai_sovrascrivere_l_originale(tmp_path):
    src = tmp_path / "foto.tif"
    Image.fromarray(np.full((24, 32, 3), 100, np.uint8)).save(src)
    image = load_image(src)
    try:
        export_image(image, analyze(image), Settings(), src)
    except ValueError:
        pass
    else:
        raise AssertionError("l'originale sarebbe stato sovrascritto")


def test_tasselli_adattivi_coprono_tutto():
    for length in (190, 600, 1000, 4000):
        for max_tile in (256, 512, 1024):
            t = adaptive_tile(length, max_tile, 32)
            assert t % 8 == 0 and t <= max(max_tile, 256 + 64)
    img = np.random.default_rng(0).random((3, 300, 700)).astype(np.float32)
    out = run_tiled(lambda t: t * 2, img, (adaptive_tile(300, 256, 32), adaptive_tile(700, 256, 32)), 32)
    assert np.allclose(out, img * 2, atol=1e-5)


def test_anteprima_di_una_zona_come_la_foto_intera(tmp_path):
    image = load_image(write_bayer_dng(tmp_path / "z", clean_linear(size=384), a=2e-3, b=2e-5, seed=2))
    analysis = analyze(image)
    s = Settings()
    s.denoise.raw_stage = "rgb"
    full = render(image, analysis, s)
    x, y, w, h = 101, 77, 150, 120
    part = render(image, analysis, s, (x, y, w, h))
    assert np.abs(part - full[y : y + h, x : x + w]).mean() < 0.004


def test_una_sola_rete_sulla_scheda_video(monkeypatch):
    """Passando da DRUNet a SCUNet e ritorno, sulla GPU resta caricata una sola sessione
    (con DirectML più sessioni insieme esaurivano la memoria e l'app si chiudeva)."""
    import threading

    from denoiser.neural import runtime

    class FakeSession:
        def __init__(self, path, device):
            self.path, self.on_gpu, self.closed = path, device != "cpu", False
            self._run_lock = threading.Lock()

        def close(self):
            self.closed = True

    monkeypatch.setattr(runtime, "InferenceSessionWrapper", FakeSession)
    monkeypatch.setattr(runtime, "_sessions", {})
    drunet = runtime.get_session("drunet.onnx")
    scunet = runtime.get_session("scunet_256.onnx")
    assert drunet.closed and not scunet.closed
    again = runtime.get_session("drunet.onnx")
    assert scunet.closed and again is not drunet and len(runtime._sessions) == 1
    cpu = runtime.get_session("scunet_256.onnx", "cpu")
    assert not again.closed and len(runtime._sessions) == 2  # le sessioni sul processore non pesano sulla GPU
    runtime.release_sessions()
    assert again.closed and cpu.closed and not runtime._sessions


def test_anteprima_della_foto_intera_come_l_esportazione(tmp_path):
    """Con la foto rimpicciolita l'anteprima elabora l'immagine intera (a strisce e in 8 bit):
    deve coincidere con il file esportato e con la zona vista da vicino."""
    import pytest

    pytest.importorskip("PySide6")
    from denoiser.gui.main_window import contains, finish_preview, preview_job
    from denoiser.gui.split_view import to_uint8

    image = load_image(write_bayer_dng(tmp_path / "intera", clean_linear(size=384), a=2e-3, b=2e-5, seed=3))
    analysis = analyze(image)
    s = Settings()
    s.denoise.raw_stage = "rgb"
    before = to_uint8(render(image, analysis, s, None, False))
    whole = (0, 0, image.width, image.height)
    result = preview_job(image, analysis, s, whole, before)
    exported = to_uint8(render(image, analysis, s))
    assert result["rgb"].dtype == np.uint8 and result["rgb"].shape == exported.shape
    assert np.abs(result["rgb"].astype(int) - exported).max() <= 1
    # rumore tolto ×4 attorno al grigio medio
    expected = np.clip((exported.astype(int) - before) * 4 + 128, 0, 255)
    assert np.abs(result["diff"].astype(int) - expected).max() <= 6
    assert result["stats"].startswith("rumore stimato")
    # Una zona (vista da vicino) ritagliata da «before» non contiguo dà lo stesso risultato.
    x, y, w, h = 64, 100, 150, 120
    part = finish_preview(image, analysis, s, result["linear"][y : y + h, x : x + w], before[y : y + h, x : x + w])
    assert np.array_equal(part["rgb"], result["rgb"][y : y + h, x : x + w])
    assert contains(whole, (x, y, w, h)) and not contains((x, y, w, h), whole)
