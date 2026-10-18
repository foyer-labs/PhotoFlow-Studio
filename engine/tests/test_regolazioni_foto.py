"""Regolazioni di ogni foto (file .denoiser) e DNG con le istruzioni per Camera Raw."""

from __future__ import annotations

import json
import os

import numpy as np
import pytest
import rawpy
import tifffile

from denoiser.batch import export_one
from denoiser.develop import camera_raw_xmp
from denoiser.io.metadata import find_exiftool
from denoiser.settings import Settings, ToneSettings
from denoiser.sidecar import (
    apply_photo_settings,
    changed_fields,
    copy_fields,
    has_settings,
    read_photo_settings,
    sidecar_path,
    write_photo_settings,
)


def test_salva_e_rilegge(tmp_path):
    photo = tmp_path / "DSC_0001.NEF"
    photo.write_bytes(b"raw")
    assert read_photo_settings(photo) is None and not has_settings(photo)
    s = Settings()
    s.tone.exposure_ev, s.tone.curve = 0.7, ((0.0, 0.05), (1.0, 1.0))
    s.develop.wb_mode, s.develop.temperature = "temperature", 4200
    s.denoise.algorithm = "nlm"
    s.output.folder = "non va salvato"
    written = write_photo_settings(photo, s)
    assert written == sidecar_path(photo) == tmp_path / "DSC_0001.NEF.denoiser"
    assert "output" not in json.loads(written.read_text(encoding="utf-8"))["impostazioni"]
    base = Settings()
    base.output.folder = "dal pannello"
    back = apply_photo_settings(base, read_photo_settings(photo))
    assert back.tone.exposure_ev == 0.7 and back.tone.curve == ((0.0, 0.05), (1.0, 1.0))
    assert back.develop.temperature == 4200 and back.denoise.algorithm == "nlm"
    assert back.output.folder == "dal pannello"


def test_file_rovinato_o_valori_strani(tmp_path):
    photo = tmp_path / "a.jpg"
    sidecar_path(photo).write_text("{ non è json", encoding="utf-8")
    assert read_photo_settings(photo) is None
    sidecar_path(photo).write_text(json.dumps({"impostazioni": {"tone": {"contrast": "tanto", "shadows": 30}}}),
                                   encoding="utf-8")
    s = apply_photo_settings(Settings(), read_photo_settings(photo))
    assert s.tone.contrast == 0 and s.tone.shadows == 30


def test_cartella_non_scrivibile(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "dati"))
    photo = tmp_path / "protetta" / "foto.jpg"
    photo.parent.mkdir()
    photo.write_bytes(b"x")
    import denoiser.sidecar as sc

    real = sc._atomic_write

    def refuse(path, text):
        if path.parent == photo.parent:
            raise PermissionError("sola lettura")
        real(path, text)

    monkeypatch.setattr(sc, "_atomic_write", refuse)
    s = Settings()
    s.tone.saturation = 25
    written = write_photo_settings(photo, s)
    assert written.parent == tmp_path / "dati" / "Denoiser" / "regolazioni"
    assert has_settings(photo) and read_photo_settings(photo)["tone"]["saturation"] == 25


def test_copia_solo_i_campi_cambiati():
    """Raffica: compensare l'esposizione su tutte senza toccare il resto di ciascuna foto."""
    before = Settings()
    after = Settings.from_dict(before.to_dict())
    after.tone.exposure_ev = -0.7
    fields = changed_fields(before.to_dict(), after.to_dict())
    assert fields == [("tone", "exposure_ev")]
    other = Settings()
    other.tone.shadows, other.develop.temperature = 40, 3900
    copied = copy_fields(other, after, fields)
    assert copied.tone.exposure_ev == -0.7 and copied.tone.shadows == 40 and copied.develop.temperature == 3900


def test_cambio_metodo_porta_i_suoi_parametri():
    a, b = Settings(), Settings()
    b.denoise.algorithm, b.denoise.params = "nlm", {"h": 1.2}
    other = Settings()
    other.denoise.params = {"wavelet": "sym4"}
    copied = copy_fields(other, b, changed_fields(a.to_dict(), b.to_dict()))
    assert copied.denoise.algorithm == "nlm" and copied.denoise.params == b.denoise.params


def test_xmp_camera_raw():
    assert camera_raw_xmp(ToneSettings()) is None
    xmp = camera_raw_xmp(ToneSettings(contrast=30, highlights=-60, shadows=45, saturation=-10)).decode()
    for text in ('crs:Contrast2012="+30"', 'crs:Highlights2012="-60"', 'crs:Shadows2012="+45"',
                 'crs:Saturation="-10"', 'crs:WhiteBalance="As Shot"'):
        assert text in xmp
    assert "ToneCurvePV2012" not in xmp
    curve = camera_raw_xmp(ToneSettings(curve=((0.0, 0.0), (0.5, 0.6), (1.0, 1.0)))).decode()
    assert "<rdf:li>0, 0</rdf:li>" in curve and "<rdf:li>128, 153</rdf:li>" in curve


def _dng_settings(tmp_path) -> Settings:
    s = Settings()
    s.output.format, s.output.folder, s.output.name_template = "dng", str(tmp_path / "uscita"), "{nome}"
    s.output.copy_metadata = False
    s.denoise.luma_strength = s.denoise.chroma_strength = 0.0  # niente denoise: il test riguarda i colori
    return s


@pytest.mark.parametrize("stage", ["sensor", "rgb"])
def test_dng_con_bilanciamento_e_istruzioni(tmp_path, dng_pair, stage):
    _clean, noisy = dng_pair
    s = _dng_settings(tmp_path)
    s.denoise.raw_stage = stage
    s.develop.wb_mode, s.develop.temperature, s.develop.tint = "temperature", 3500, 10
    s.tone.contrast, s.tone.shadows = 25, 30
    outcome, saved, _ = export_one(noisy, s)
    assert outcome == "ok"
    with tifffile.TiffFile(saved) as tif:
        tags = tif.pages[0].tags
        xmp = tags[700].value
        xmp = xmp.decode() if isinstance(xmp, bytes) else xmp
        assert 'crs:Contrast2012="+25"' in xmp and 'crs:Shadows2012="+30"' in xmp
        neutral = np.array(tags[50728].value, dtype=float).reshape(-1, 2)
        neutral = neutral[:, 0] / neutral[:, 1]
    with rawpy.imread(str(saved)) as raw:
        rgb = raw.postprocess(use_camera_wb=True, no_auto_bright=True, gamma=(1, 1), output_bps=16)
    mean = rgb.reshape(-1, 3).mean(axis=0)
    if stage == "sensor":
        assert not np.allclose(neutral, 1.0, atol=0.02)  # il bilanciamento scelto, non quello dello scatto
    # Luce assunta a 3500 K: la foto (neutra a 6500 K) diventa più fredda.
    assert mean[2] > mean[0]


@pytest.mark.skipif(find_exiftool() is None and not os.environ.get("DENOISER_EXIFTOOL"), reason="ExifTool non installato")
def test_exiftool_non_sovrascrive_bilanciamento_e_istruzioni(tmp_path, dng_pair):
    _clean, noisy = dng_pair
    s = _dng_settings(tmp_path)
    s.output.copy_metadata = True
    s.develop.wb_mode, s.develop.temperature = "temperature", 3500
    s.tone.contrast = 40
    _, saved, notes = export_one(noisy, s)
    assert not [n for n in notes if "ExifTool" in n]
    with tifffile.TiffFile(saved) as tif:
        xmp = tif.pages[0].tags[700].value
        xmp = xmp.decode() if isinstance(xmp, bytes) else xmp
        neutral = np.array(tif.pages[0].tags[50728].value, dtype=float).reshape(-1, 2)
    assert "Contrast2012" in xmp and "+40" in xmp
    assert not np.allclose(neutral[:, 0] / neutral[:, 1], 1.0, atol=0.02)
