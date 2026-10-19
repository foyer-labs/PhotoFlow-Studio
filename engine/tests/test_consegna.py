"""Metadati di consegna: autore, diritti, parole chiave nelle foto esportate (PhotoFlow Studio, M2)."""

from __future__ import annotations

import subprocess

import numpy as np
import pytest
import tifffile

from denoiser.io.metadata import delivery_arguments, find_exiftool
from denoiser.server import Engine

METADATA = {
    "creator": "Luca Rossi", "rights": "© 2025 Luca Rossi", "usage_terms": "Solo uso personale", "email": "studio@example.com",
    "url": "https://example.com", "keywords": ["mare", "spiaggia", "mare"], "hierarchical": ["Luoghi|Spiaggia"],
    "people": ["Giulia"], "description": "Una spiaggia\ncon ombrelloni", "rating": 5,
}


def test_argomenti_di_consegna():
    args = delivery_arguments(METADATA)
    assert "-XMP-dc:Creator=Luca Rossi" in args and "-EXIF:Artist=Luca Rossi" in args
    assert "-XMP-dc:Rights=© 2025 Luca Rossi" in args and "-XMP-xmpRights:Marked=True" in args
    assert args.count("-XMP-dc:Subject+=mare") == 1  # niente doppioni
    assert "-XMP-iptcExt:PersonInImage+=Giulia" in args
    assert "-XMP-dc:Description=Una spiaggia con ombrelloni" in args  # una riga sola
    assert "-XMP-xmp:Rating=5" in args
    assert delivery_arguments({}) == [] and delivery_arguments({"rating": 0, "keywords": [" "]}) == []


@pytest.mark.skipif(find_exiftool() is None, reason="ExifTool non installato")
def test_esportazione_con_autore_e_parole_chiave(tmp_path):
    photo = tmp_path / "foto.tif"
    tifffile.imwrite(photo, (np.random.default_rng(1).random((80, 120, 3)) * 65535).astype(np.uint16), photometric="rgb")
    engine = Engine(tmp_path / "anteprime")
    settings = engine.cmd_open(str(photo))["settings"]
    output = {"format": "jpeg", "bit_depth": 8, "folder": str(tmp_path / "out"), "name_template": "{nome}", "copy_metadata": True}
    result = engine.cmd_export([{"path": str(photo), "settings": settings, "metadata": METADATA}], output)["results"][0]
    assert result["outcome"] == "ok", result
    out = subprocess.run([str(find_exiftool()), "-s3", "-charset", "utf8", "-XMP-dc:Creator", "-XMP-dc:Rights", "-XMP-dc:Subject",
                          "-XMP-iptcExt:PersonInImage", result["output"]], capture_output=True)
    text = out.stdout.decode("utf-8", "replace")
    assert "Luca Rossi" in text and "© 2025 Luca Rossi" in text and "mare, spiaggia" in text and "Giulia" in text
