"""Motore a richieste usato da PhotoFlow Studio."""

from __future__ import annotations

import io
import json
import subprocess
import sys
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

from denoiser.server import Cancelled, Engine, Server
from denoiser.settings import Settings


@pytest.fixture()
def engine(tmp_path):
    return Engine(tmp_path / "anteprime")


@pytest.fixture()
def raw(tmp_path, dng_pair):
    """Copia del DNG rumoroso in una cartella di prova (le regolazioni vengono scritte accanto)."""
    target = tmp_path / "foto" / "DSC_0001.dng"
    target.parent.mkdir()
    target.write_bytes(dng_pair[1].read_bytes())
    return str(target)


def test_hello(engine):
    info = engine.cmd_hello()
    keys = [a["key"] for a in info["algorithms"]]
    assert "wavelet" in keys and "drunet" in keys
    assert info["defaults"]["output"]["format"] == "tiff"
    assert "temperature" in info["choices"]["wb_modes"]


def test_apre_e_crea_le_anteprime(engine, raw):
    info = engine.cmd_open(raw)
    assert info["is_raw"] and info["width"] > 0 and "camera" in info["wb_equivalents"]
    assert not info["has_settings"]
    s = info["settings"]
    fit = engine.cmd_render(raw, s, max_side=128)
    assert max(fit["width"], fit["height"]) == 128 and Path(fit["before"]).is_file() and "after" not in fit
    region = engine.cmd_render(raw, s, region=[10, 20, 64, 48], denoise=True)
    assert region["region"] == [10, 20, 64, 48]
    after = np.asarray(Image.open(region["after"]))
    before = np.asarray(Image.open(region["before"]))
    assert after.shape == before.shape == (48, 64, 3)
    assert Path(region["difference"]).is_file()
    assert region["reduction"] > 20  # la foto di prova è molto rumorosa
    # Zoom sotto il 100% (PhotoFlow Studio S25): la zona ridotta ai pixel dello schermo, prima e dopo uguali.
    small = engine.cmd_render(raw, s, region=[0, 0, 128, 96], max_side=64, denoise=True)
    assert small["region"] == [0, 0, 128, 96]
    assert np.asarray(Image.open(small["before"])).shape == np.asarray(Image.open(small["after"])).shape == (48, 64, 3)
    assert Path(small["difference"]).is_file()


def test_istogramma_e_contagocce(engine, raw):
    s = engine.cmd_open(raw)["settings"]
    h = np.array(engine.cmd_histogram(raw, s)["histogram"])
    assert h.shape == (4, 256) and h.sum() > 0
    s["tone"]["exposure_ev"] = 2.0
    brighter = np.array(engine.cmd_histogram(raw, s)["histogram"])
    assert (brighter[3] * np.arange(256)).sum() / brighter[3].sum() > (h[3] * np.arange(256)).sum() / h[3].sum()
    wb = engine.cmd_pick_wb(raw, 100, 100)
    assert 2000 <= wb["temperature"] <= 15000


def test_regolazioni_per_foto_e_raffica(engine, raw, tmp_path):
    other = tmp_path / "foto" / "DSC_0002.dng"
    other.write_bytes(Path(raw).read_bytes())
    before = Settings().to_dict()
    after = Settings().to_dict()
    after["tone"]["exposure_ev"] = -0.7
    engine.cmd_save_settings(raw, after)
    loaded = engine.cmd_load_settings(raw)
    assert loaded["has_settings"] and loaded["settings"]["tone"]["exposure_ev"] == -0.7
    other_settings = Settings()
    other_settings.tone.shadows = 40
    engine.cmd_save_settings(str(other), other_settings.to_dict())
    synced = engine.cmd_sync_settings(before, after, [str(other)])
    assert synced["fields"] == [["tone", "exposure_ev"]] and synced["written"] == [str(other)]
    back = engine.cmd_load_settings(str(other))["settings"]["tone"]
    assert back["exposure_ev"] == -0.7 and back["shadows"] == 40
    # All'apertura la foto ritrova le sue regolazioni
    assert engine.cmd_open(raw)["settings"]["tone"]["exposure_ev"] == -0.7


def test_esportazione_con_avanzamento_e_annullamento(engine, raw, tmp_path):
    out = {"format": "tiff", "folder": str(tmp_path / "uscita"), "name_template": "{nome}", "copy_metadata": False}
    steps = []
    result = engine.cmd_export([{"path": raw, "settings": Settings().to_dict()}], out,
                               progress=lambda f, m: steps.append(f))
    assert result["results"][0]["outcome"] == "ok"
    assert Path(result["results"][0]["output"]).name == "DSC_0001.tif"
    assert steps and max(steps) <= 1.0

    def stop(_f, _m):
        raise Cancelled()

    with pytest.raises(Cancelled):
        engine.cmd_export([{"path": raw, "settings": {}}], {**out, "folder": str(tmp_path / "altra")}, progress=stop)
    assert not (tmp_path / "altra").exists() or not any((tmp_path / "altra").iterdir())


def test_canale_json(engine, raw):
    requests = [
        {"id": 1, "cmd": "hello"},
        {"id": 2, "cmd": "open", "path": raw},
        {"id": 3, "cmd": "inesistente"},
        {"id": 4, "cmd": "close", "path": raw},
    ]
    stdin = io.StringIO("".join(json.dumps(r) + "\n" for r in requests) + json.dumps({"cmd": "quit"}) + "\n")
    stdout = io.StringIO()
    server = Server(engine, stdin, stdout)
    server.run()
    import time

    deadline = time.time() + 30
    while time.time() < deadline and stdout.getvalue().count("\n") < 4:
        time.sleep(0.05)
    replies = {m["id"]: m for m in map(json.loads, stdout.getvalue().splitlines())}
    assert replies[1]["ok"] and replies[2]["ok"] and replies[4]["ok"]
    assert not replies[3]["ok"] and "sconosciuto" in replies[3]["error"]


def test_processo_separato():
    """Come lo avvia PhotoFlow Studio: messaggio «ready», poi risposte riga per riga."""
    proc = subprocess.Popen([sys.executable, "-m", "denoiser.server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
    try:
        ready = json.loads(proc.stdout.readline())
        assert ready["event"] == "ready"
        proc.stdin.write(json.dumps({"id": 1, "cmd": "hello"}) + "\n")
        proc.stdin.flush()
        reply = json.loads(proc.stdout.readline())
        assert reply["id"] == 1 and reply["ok"]
        proc.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
        proc.stdin.flush()
        assert proc.wait(timeout=30) == 0
    finally:
        if proc.poll() is None:
            proc.kill()


def test_ricette_di_consegna(engine, raw, tmp_path):
    """Web (lato lungo ridotto, nome con l'evento), Lightroom «solo pulizia» (DNG senza istruzioni di tono)."""
    import tifffile

    info = engine.cmd_open(raw)
    assert info["suggested_preset"] in ("Delicato", "Bilanciato", "Forte (ISO alti)")
    s = info["settings"]
    s["tone"]["contrast"] = 30
    web = {"format": "jpeg", "jpeg_quality": 85, "max_side": 64, "strip_gps": True, "copy_metadata": False,
           "folder": str(tmp_path / "web"), "name_template": "{evento}_{seq}_{nome}"}
    result = engine.cmd_export([{"path": raw, "settings": s, "name_values": {"evento": "Monza", "seq": "007"}}], web)
    out = Path(result["results"][0]["output"])
    assert out.name == "Monza_007_DSC_0001.jpg"
    assert max(Image.open(out).size) == 64

    dng = {"format": "dng", "copy_metadata": False, "folder": str(tmp_path / "lr"), "name_template": "{nome}-PF-NR"}
    with_tone = Path(engine.cmd_export([{"path": raw, "settings": s}], dng)["results"][0]["output"])
    only_clean = Path(engine.cmd_export([{"path": raw, "settings": s}], {**dng, "camera_raw": False,
                                                                          "folder": str(tmp_path / "lr2")})["results"][0]["output"])
    assert with_tone.name == "DSC_0001-PF-NR.dng"
    with tifffile.TiffFile(with_tone) as tif:
        assert 700 in tif.pages[0].tags
    with tifffile.TiffFile(only_clean) as tif:
        assert 700 not in tif.pages[0].tags
