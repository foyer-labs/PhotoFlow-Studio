"""Carosello ed esportazione di più foto: miniature, formati per le foto non RAW, nomi dei file."""

from __future__ import annotations

import numpy as np
import pytest
import tifffile
from PIL import Image

from denoiser.batch import collect_inputs, export_one, is_source, settings_for
from denoiser.io.thumbnail import make_thumbnail
from denoiser.settings import Settings
from synthetic import clean_display


def _jpeg(path, orientation: int = 1, size=(96, 64)) -> None:
    rgb = (clean_display(size=max(size))[: size[1], : size[0]] * 255).astype(np.uint8)
    exif = Image.Exif()
    exif[0x0112] = orientation
    Image.fromarray(rgb).save(path, quality=92, exif=exif.tobytes())


def _settings(fmt: str = "tiff", folder: str = "denoised") -> Settings:
    s = Settings()
    s.output.format = fmt
    s.output.folder = folder
    s.output.name_template = "{nome}"
    s.output.copy_metadata = False
    return s


def test_miniatura_raw_senza_anteprima(dng_pair):
    _clean, noisy = dng_pair
    thumb = make_thumbnail(noisy, size=64)
    assert thumb.dtype == np.uint8 and thumb.ndim == 3 and thumb.shape[2] == 3
    assert max(thumb.shape[:2]) == 64


def test_miniatura_jpeg_ruotata(tmp_path):
    path = tmp_path / "verticale.jpg"
    _jpeg(path, orientation=6)  # 96×64 da ruotare di 90°: diventa verticale
    thumb = make_thumbnail(path, size=48)
    h, w = thumb.shape[:2]
    assert h == 48 and w < h


def test_miniatura_tiff_16_bit(tmp_path):
    path = tmp_path / "sedici.tif"
    tifffile.imwrite(path, (clean_display(size=80) * 65535).astype(np.uint16), photometric="rgb")
    thumb = make_thumbnail(path, size=40)
    assert thumb.dtype == np.uint8 and max(thumb.shape[:2]) == 40


def test_cartella_del_carosello(tmp_path, dng_pair):
    _jpeg(tmp_path / "b.jpg")
    _jpeg(tmp_path / "a.jpg")
    (tmp_path / "note.txt").write_text("x")
    (tmp_path / "c.parziale-1234.tif").write_bytes(b"")
    (tmp_path / "denoised").mkdir()
    _jpeg(tmp_path / "denoised" / "a.jpg")
    assert [p.name for p in collect_inputs([tmp_path])] == ["a.jpg", "b.jpg"]


def test_dng_solo_per_i_raw(tmp_path):
    s = _settings("dng")
    assert settings_for(tmp_path / "foto.NEF", s, "png").output.format == "dng"
    assert settings_for(tmp_path / "foto.jpg", s, "png").output.format == "png"
    assert s.output.format == "dng"  # le impostazioni di partenza non cambiano
    tiff = _settings("tiff")
    assert settings_for(tmp_path / "foto.jpg", tiff, "png") is tiff


def test_esportazione_mantiene_il_nome(tmp_path, dng_pair):
    _clean, noisy = dng_pair
    s = _settings("tiff", folder=str(tmp_path / "uscita"))
    outcome, saved, _notes = export_one(noisy, s)
    assert outcome == "ok"
    assert saved == tmp_path / "uscita" / "noisy.tif" and saved.is_file()


def test_originale_mai_sovrascritto(tmp_path):
    original = tmp_path / "foto.jpg"
    _jpeg(original)
    before = original.read_bytes()
    s = _settings("jpeg", folder="")  # stessa cartella, stesso nome, stesso formato
    assert is_source(tmp_path / "foto.jpg", original)
    for policy in ("overwrite", "rename", "skip"):
        outcome, saved, _ = export_one(original, s, policy)
        assert outcome == "ok" and saved != original
        assert saved.parent == tmp_path and saved.name.startswith("foto_")
    assert original.read_bytes() == before


@pytest.mark.parametrize("policy, outcome, name", [
    ("skip", "saltata", "foto.png"), ("rename", "ok", "foto_1.png"), ("overwrite", "ok", "foto.png"),
])
def test_file_esistente(tmp_path, policy, outcome, name):
    original = tmp_path / "foto.jpg"
    _jpeg(original)
    out = tmp_path / "denoised"
    out.mkdir()
    (out / "foto.png").write_bytes(b"vecchio")
    result, saved, _ = export_one(original, _settings("png"), policy)
    assert result == outcome and saved.name == name
    if policy == "overwrite":
        assert (out / "foto.png").read_bytes() != b"vecchio"


def test_stesso_nome_nella_stessa_esportazione(tmp_path):
    _jpeg(tmp_path / "foto.jpg")
    first = tmp_path / "denoised" / "foto.tif"
    first.parent.mkdir()
    first.write_bytes(b"prodotto da IMG.NEF nella stessa esportazione")
    _, saved, _ = export_one(tmp_path / "foto.jpg", _settings(), "overwrite", taken=frozenset({first}))
    assert saved.name == "foto_1.tif"
