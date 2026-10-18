import json

from denoiser.cli import main
from denoiser.presets import BUILTIN_PRESETS, load_preset, resolve_preset, save_preset
from denoiser.settings import Settings


def test_preset_roundtrip(tmp_path):
    s = Settings()
    s.denoise.algorithm = "nlm"
    s.denoise.params = {"search_window": 15}
    s.develop.wb_custom = (1.9, 1.0, 1.4)
    path = save_preset(s, tmp_path / "mio")
    assert path.suffix == ".json"
    loaded = load_preset(path)
    assert loaded == s


def test_unknown_keys_are_ignored(tmp_path):
    path = tmp_path / "vecchio.json"
    path.write_text(json.dumps({"denoise": {"algorithm": "bilateral", "chiave_rimossa": 1}, "extra": {}}))
    assert load_preset(path).denoise.algorithm == "bilateral"


def test_builtin_presets_resolve():
    for name in BUILTIN_PRESETS:
        assert resolve_preset(name.lower()).denoise.algorithm


def test_cli_elabora(tmp_path, dng_pair, capsys):
    _, noisy = dng_pair
    target = tmp_path / "in.dng"
    target.write_bytes(noisy.read_bytes())
    code = main([
        "elabora", str(target), "--algoritmo", "nlm", "--param", "search_window=11",
        "--formato", "jpeg", "--cartella", str(tmp_path / "out"), "--nome", "{nome}_{metodo}",
    ])
    assert code == 0
    assert (tmp_path / "out" / "in_nlm.jpg").is_file()


def test_cli_rejects_unknown_param(tmp_path, dng_pair):
    _, noisy = dng_pair
    try:
        main(["elabora", str(noisy), "--param", "inesistente=1"])
    except SystemExit as exc:
        assert "inesistente" in str(exc)
    else:  # pragma: no cover
        raise AssertionError("atteso errore")


def test_cli_analizza(dng_pair, capsys):
    _, noisy = dng_pair
    assert main(["analizza", str(noisy)]) == 0
    out = capsys.readouterr().out
    assert "Bayer RGGB" in out
    assert "livello 3" in out


def test_builtin_preset_changes_only_declared_fields():
    from denoiser.presets import apply_preset

    current = Settings()
    current.denoise.raw_stage = "rgb"
    current.denoise.hot_pixel_threshold = 8
    current.output.format = "jpeg"
    _, content = BUILTIN_PRESETS["Forte (ISO alti)"]
    result = apply_preset(current, content)
    assert result.denoise.algorithm == "nlm"
    assert result.denoise.hot_pixel_threshold == 8
    # La fase per i RAW torna quella consigliata: un preset non deve lasciare DRUNet «dopo lo sviluppo».
    assert result.denoise.raw_stage == "sensor"
    assert result.output.format == "jpeg"


def test_user_preset_sections_and_names(tmp_path):
    from denoiser.presets import apply_preset, clean_name, read_preset

    s = Settings()
    s.denoise.algorithm = "bilateral"
    s.output.format = "png"
    s.output.overwrite = True
    path = save_preset(s, tmp_path / "ISO 6400 v1.5", sections=("denoise",))
    assert path.name == "ISO 6400 v1.5.json"
    data = read_preset(path)
    assert set(data) == {"denoise"}
    current = Settings()
    current.output.format = "tiff"
    assert apply_preset(current, data).output.format == "tiff"
    full = save_preset(s, tmp_path / "tutto")
    assert read_preset(full)["output"]["overwrite"] is False
    assert clean_name("a/b:c") == "a_b_c"
