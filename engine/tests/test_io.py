from pathlib import Path

import numpy as np
import pytest
from PIL import Image

from denoiser.batch import collect_inputs, process_file
from denoiser.image import ImageMeta
from denoiser.io import UnsupportedImageError, load_image, save_image
from denoiser.io.naming import output_path, render_name, unique_path
from denoiser.settings import OutputSettings, Settings
from synthetic import clean_display


@pytest.mark.parametrize("fmt,bits", [("tiff", 16), ("tiff", 8), ("png", 16), ("png", 8)])
def test_lossless_roundtrip(tmp_path, fmt, bits):
    img = clean_display(64)
    path = save_image(img, tmp_path / f"out.{fmt}", fmt, bit_depth=bits)
    loaded = load_image(path)
    step = 1 / 255 if bits == 8 else 1 / 65535
    assert loaded.pixels.shape == img.shape
    assert np.abs(loaded.pixels - img).max() <= step


def test_jpeg_roundtrip_and_alpha_png(tmp_path):
    img = clean_display(64)
    path = save_image(img, tmp_path / "out.jpg", "jpeg", jpeg_quality=95)
    assert np.abs(load_image(path).pixels - img).mean() < 0.02

    alpha = np.linspace(0, 1, 64 * 64, dtype=np.float32).reshape(64, 64)
    path = save_image(img, tmp_path / "alpha.png", "png", bit_depth=16, alpha=alpha)
    loaded = load_image(path)
    assert loaded.alpha is not None
    assert np.abs(loaded.alpha - alpha).max() < 1e-4


def test_unicode_path(tmp_path):
    folder = tmp_path / "foto è già"
    path = save_image(clean_display(32), folder / "città.png", "png")
    assert load_image(path).pixels.shape == (32, 32, 3)


def test_grayscale_png_becomes_rgb(tmp_path):
    path = tmp_path / "gray.png"
    Image.fromarray((np.linspace(0, 255, 32 * 32).reshape(32, 32)).astype(np.uint8)).save(path)
    loaded = load_image(path)
    assert loaded.pixels.shape == (32, 32, 3)


def test_unsupported_and_missing_files(tmp_path):
    with pytest.raises(UnsupportedImageError):
        load_image(tmp_path / "manca.nef")
    bogus = tmp_path / "finto.nef"
    bogus.write_bytes(b"non sono un raw")
    with pytest.raises(UnsupportedImageError) as exc:
        load_image(bogus, use_dng_converter=False)
    assert "Adobe DNG Converter" in str(exc.value)


def test_naming_template(tmp_path):
    meta = ImageMeta(source=tmp_path / "DSC_0001.NEF", kind="raw", iso=6400, date="20260110")
    out = OutputSettings(format="tiff", folder="denoised", name_template="{nome}_{metodo}_{iso}_{data}")
    assert output_path(meta, out, "wavelet") == tmp_path / "denoised" / "DSC_0001_wavelet_6400_20260110.tif"
    assert render_name("{nome}:{sconosciuto}", meta, "nlm") == "DSC_0001_{sconosciuto}"
    existing = tmp_path / "a.tif"
    existing.write_bytes(b"")
    assert unique_path(existing) == tmp_path / "a_1.tif"


def test_batch_on_folder(tmp_path, dng_pair):
    _, noisy = dng_pair
    folder = tmp_path / "foto"
    folder.mkdir()
    (folder / "scatto.dng").write_bytes(noisy.read_bytes())
    save_image(clean_display(48), folder / "web.jpg", "jpeg")
    (folder / "note.txt").write_text("ignorami")
    files = collect_inputs([folder])
    assert sorted(f.name for f in files) == ["scatto.dng", "web.jpg"]

    settings = Settings()
    settings.output.format = "png"
    results = [process_file(f, settings) for f in files]
    assert all(r.ok for r in results), [r.error for r in results]
    outputs = sorted(p.name for p in (folder / "denoised").iterdir())
    assert outputs == ["scatto_dn.png", "web_dn.png"]
    # Una seconda esecuzione non sovrascrive.
    again = process_file(files[0], settings)
    assert again.output.name == "scatto_dn_1.png"


def test_dng_export_roundtrip(tmp_path, dng_pair):
    from denoiser.batch import export_image
    from denoiser.io.raw import decode_raw
    from denoiser.pipeline import analyze, denoise_region

    _, noisy = dng_pair
    image = load_image(noisy)
    analysis = analyze(image)
    for stage in ("sensor", "rgb"):
        settings = Settings()
        settings.output.format = "dng"
        settings.denoise.raw_stage = stage
        path = export_image(image, analysis, settings, tmp_path / f"{stage}.dng")
        back = decode_raw(path, measure_noise=False)
        expected = denoise_region(image, analysis, settings)
        assert back.pixels.shape == expected.shape
        assert np.abs(back.pixels - expected).max() < 1e-3


def test_dng_export_refused_for_jpeg(tmp_path):
    from denoiser.batch import process_file

    src = save_image(clean_display(32), tmp_path / "foto.jpg", "jpeg")
    settings = Settings()
    settings.output.format = "dng"
    result = process_file(src, settings)
    assert not result.ok and "solo per le foto RAW" in result.error


def test_bayer_dng_with_camera_matrix(tmp_path, dng_pair):
    """Percorso dei RAW proprietari: matrice colore e bilanciamento presi da LibRaw."""
    from denoiser.io.dng import XYZ_TO_SRGB, SensorData, write_bayer_dng
    from denoiser.io.raw import decode_raw

    _, noisy = dng_pair
    original = decode_raw(noisy, measure_noise=False)
    import rawpy

    with rawpy.imread(str(noisy)) as raw:
        cfa = raw.raw_image_visible.copy()
    sensor = SensorData(
        cfa=cfa, pattern=np.array([[0, 1], [1, 2]], dtype=np.uint8), black=np.full((2, 2), 256.0),
        white=4095, xyz_to_camera=XYZ_TO_SRGB, camera_neutral=np.ones(3), flip=6,
    )
    path = write_bayer_dng(tmp_path / "camera.dng", sensor, "Marca", "Modello")
    back = decode_raw(path, measure_noise=False)
    # Orientamento 6: l'immagine riletta è ruotata di 90° in senso orario.
    assert back.pixels.shape == (original.width, original.height, 3)
    diff = np.abs(back.pixels - np.rot90(original.pixels, -1))
    # Pochi pixel isolati possono differire per le scelte locali della demosaicizzazione AHD.
    assert diff.mean() < 1e-3 and np.percentile(diff, 99) < 5e-3
    assert back.meta.camera == "Marca Modello"


@pytest.mark.skipif(__import__("shutil").which("exiftool") is None, reason="ExifTool non installato")
def test_metadata_copied_with_exiftool(tmp_path):
    import subprocess

    from denoiser.batch import process_file

    src = tmp_path / "originale.jpg"
    exif = Image.Exif()
    exif[0x010F], exif[0x0110] = "Nikon", "Z 5_2"
    exif.get_ifd(0x8769)[0x8827] = 6400
    Image.fromarray((clean_display(64) * 255).astype(np.uint8)).save(src, exif=exif.tobytes())
    subprocess.run(["exiftool", "-overwrite_original", "-q", "-XMP-dc:Title=Prova", "-GPSLatitude=45.5",
                    "-GPSLatitudeRef=N", str(src)], check=True)
    for fmt in ("tiff", "png", "jpeg"):
        settings = Settings()
        settings.output.format = fmt
        result = process_file(src, settings)
        assert result.ok, result.error
        out = subprocess.run(["exiftool", "-s", "-Model", "-ISO", "-Title", "-GPSLatitude", "-Software",
                              str(result.output)], capture_output=True, text=True).stdout
        assert "Z 5_2" in out and "6400" in out and "Prova" in out and "45" in out and "Denoiser" in out, out


# ----------------------------------------------------------------------------- NEF «alta efficienza»


def test_dati_non_decodificabili_diventano_non_supportati(tmp_path, monkeypatch):
    """LibRaw apre i NEF «alta efficienza» ma fallisce leggendo i dati: va proposta la conversione,
    non un errore generico."""
    import rawpy

    from denoiser.io import raw as raw_module

    class Unreadable:
        sizes = type("S", (), {"flip": 0})()

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

        @property
        def rgb_xyz_matrix(self):
            raise rawpy.LibRawDataError("Data error or unsupported file format")

    src = tmp_path / "DSC_0035.NEF"
    src.write_bytes(b"x" * 2048)
    monkeypatch.setattr(raw_module.rawpy, "imread", lambda _f: Unreadable())
    with pytest.raises(raw_module.RawNotSupportedError, match="decodificare"):
        raw_module.decode_raw(src)


def _fake_unreadable_nef(tmp_path, monkeypatch):
    from denoiser.io import loader

    src = tmp_path / "DSC_0035.NEF"
    src.write_bytes(b"x" * 2048)
    real_decode = loader.decode_raw

    def decode(path, develop=None):
        if path.suffix.lower() == ".nef":
            raise loader.RawNotSupportedError("LibRaw non sa decodificare i dati del sensore di questo file.")
        return real_decode(path, develop)

    monkeypatch.setattr(loader, "decode_raw", decode)
    monkeypatch.setattr(loader, "is_nikon_high_efficiency", lambda _p: True)
    return src


def test_nef_alta_efficienza_senza_convertitore_spiega_cosa_fare(tmp_path, monkeypatch):
    from denoiser.io import loader

    src = _fake_unreadable_nef(tmp_path, monkeypatch)
    monkeypatch.setattr(loader.dng_converter, "find_converter", lambda _p=None: None)
    with pytest.raises(UnsupportedImageError) as info:
        load_image(src)
    text = str(info.value)
    assert "alta efficienza" in text and loader.ADOBE_DNG_CONVERTER_URL in text and "senza perdita" in text


def test_nef_alta_efficienza_passa_da_dng_converter(tmp_path, monkeypatch):
    from denoiser.io import loader
    from synthetic import clean_linear, write_bayer_dng

    src = _fake_unreadable_nef(tmp_path, monkeypatch)
    converted = write_bayer_dng(tmp_path / "convertito", clean_linear(size=128))
    monkeypatch.setattr(loader.dng_converter, "find_converter", lambda _p=None: tmp_path / "Adobe DNG Converter.exe")
    monkeypatch.setattr(loader.dng_converter, "convert_to_dng", lambda _src, _conv: converted)
    image = load_image(src)
    assert image.meta.source == src and image.meta.kind == "dng-convertito"
    assert any("alta efficienza" in note for note in image.meta.notes)
    assert image.raw_source is not None  # DNG Bayer: anche il livello 3 è disponibile


def test_dng_converter_chiede_dng_leggibili_da_libraw(tmp_path, monkeypatch):
    import subprocess

    from denoiser.io import dng_converter

    calls = []

    def fake_run(cmd, **_kwargs):
        calls.append(cmd)
        out = Path(cmd[cmd.index("-d") + 1])
        (out / "DSC_0035.dng").write_bytes(b"dng")
        return subprocess.CompletedProcess(cmd, 0, b"", b"")

    src = tmp_path / "DSC_0035.NEF"
    src.write_bytes(b"nef")
    monkeypatch.setattr(dng_converter.subprocess, "run", fake_run)
    result = dng_converter.convert_to_dng(src, tmp_path / "conv.exe", cache_dir=tmp_path / "cache")
    assert result.name == "DSC_0035.dng" and result.read_bytes() == b"dng"
    assert "-dng1.4" in calls[0] and "-c" in calls[0]
