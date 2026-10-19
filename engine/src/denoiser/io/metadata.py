"""Copia completa dei metadati (EXIF, XMP, IPTC, GPS, obiettivo…) con ExifTool.

ExifTool è incluso nel pacchetto per Windows; in alternativa viene cercato nel PATH
o nel percorso indicato dalla variabile d'ambiente DENOISER_EXIFTOOL.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

from .. import __version__

ENV_VAR = "DENOISER_EXIFTOOL"

# Tag da non copiare: l'orientamento è già applicato o scritto da noi, la descrizione
# contiene le impostazioni di denoise, le anteprime incorporate non corrispondono più. Bilanciamento,
# esposizione di base e impostazioni di Camera Raw dei DNG li scrive Denoiser dalle regolazioni.
_EXCLUDED = ("Orientation", "ImageDescription", "ThumbnailImage", "PreviewImage", "JpgFromRaw", "OtherImage",
             "AsShotNeutral", "BaselineExposure", "XMP-crs:all")


def _bundled_candidates() -> list[Path]:
    roots = []
    if getattr(sys, "frozen", False):
        roots.append(Path(sys.executable).parent)
        if hasattr(sys, "_MEIPASS"):
            roots.append(Path(sys._MEIPASS))  # type: ignore[attr-defined]
    names = ("exiftool.exe", "exiftool")
    return [root / "exiftool" / name for root in roots for name in names]


def find_exiftool(explicit: str | os.PathLike | None = None) -> Path | None:
    for candidate in (explicit, os.environ.get(ENV_VAR), *_bundled_candidates()):
        if candidate and Path(candidate).is_file():
            return Path(candidate)
    found = shutil.which("exiftool")
    return Path(found) if found else None


def read_tag(path: Path, tag: str, exiftool: Path | None = None) -> str | None:
    """Valore testuale di un tag (es. "NEFCompression"), oppure None se ExifTool manca o il tag non c'è."""
    exiftool = exiftool or find_exiftool()
    if exiftool is None:
        return None
    kwargs = {}
    if sys.platform == "win32":
        kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
    args = ["-charset", "filename=utf8", "-s3", f"-{tag}", str(path)]
    try:
        result = subprocess.run(
            [str(exiftool), "-@", "-"], input=("\n".join(args) + "\n").encode("utf-8"),
            capture_output=True, timeout=30, **kwargs
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    value = result.stdout.decode("utf-8", "replace").strip()
    return value or None


def _run(exiftool: Path, args: list[str], timeout: int) -> str | None:
    # Gli argomenti arrivano da stdin in UTF-8 («-@ -»): funziona con lettere accentate nei nomi dei
    # file e nel nome utente su Windows, senza file temporanei.
    kwargs = {}
    if sys.platform == "win32":
        kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
    try:
        result = subprocess.run(
            [str(exiftool), "-@", "-"], input=("\n".join(args) + "\n").encode("utf-8"),
            capture_output=True, timeout=timeout, **kwargs
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return f"ExifTool non è riuscito a scrivere i metadati: {exc}"
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", "replace").strip().splitlines()
        return "ExifTool: " + (detail[-1] if detail else f"codice {result.returncode}")
    return None


def _line(value) -> str:
    return str(value).replace("\r", " ").replace("\n", " ").strip()


def delivery_arguments(metadata: dict) -> list[str]:
    """Argomenti di ExifTool per i metadati di consegna (PhotoFlow Studio, M2): autore, diritti, contatti,
    parole chiave, persone, didascalia e stelle, in XMP e, dove il formato li ha, in EXIF e IPTC."""
    args: list[str] = []
    if metadata.get("creator"):
        creator = _line(metadata["creator"])
        args += [f"-XMP-dc:Creator={creator}", f"-EXIF:Artist={creator}", f"-IPTC:By-line={creator}"]
    if metadata.get("rights"):
        rights = _line(metadata["rights"])
        args += [f"-XMP-dc:Rights={rights}", f"-EXIF:Copyright={rights}", f"-IPTC:CopyrightNotice={rights}",
                 "-XMP-xmpRights:Marked=True"]
    if metadata.get("usage_terms"):
        args.append(f"-XMP-xmpRights:UsageTerms={_line(metadata['usage_terms'])}")
    if metadata.get("email"):
        args.append(f"-XMP-iptcCore:CreatorWorkEmail={_line(metadata['email'])}")
    if metadata.get("url"):
        args.append(f"-XMP-iptcCore:CreatorWorkURL={_line(metadata['url'])}")
    for keyword in dict.fromkeys(_line(k) for k in metadata.get("keywords") or [] if _line(k)):
        args += [f"-XMP-dc:Subject-={keyword}", f"-XMP-dc:Subject+={keyword}", f"-IPTC:Keywords-={keyword}",
                 f"-IPTC:Keywords+={keyword}"]
    for keyword in dict.fromkeys(_line(k) for k in metadata.get("hierarchical") or [] if _line(k)):
        args += [f"-XMP-lr:HierarchicalSubject-={keyword}", f"-XMP-lr:HierarchicalSubject+={keyword}"]
    for person in dict.fromkeys(_line(p) for p in metadata.get("people") or [] if _line(p)):
        args += [f"-XMP-iptcExt:PersonInImage-={person}", f"-XMP-iptcExt:PersonInImage+={person}"]
    if metadata.get("description"):
        description = _line(metadata["description"])
        args += [f"-XMP-dc:Description={description}", f"-IPTC:Caption-Abstract={description}"]
    rating = metadata.get("rating")
    if isinstance(rating, int) and 1 <= rating <= 5:
        args.append(f"-XMP-xmp:Rating={rating}")
    return args


def write_delivery_metadata(target: Path, metadata: dict, exiftool: Path | None = None) -> str | None:
    """Scrive in ``target`` i metadati di consegna. Restituisce un messaggio d'errore o None."""
    tags = delivery_arguments(metadata or {})
    if not tags:
        return None
    exiftool = exiftool or find_exiftool()
    if exiftool is None:
        return "ExifTool non trovato: autore, copyright e parole chiave non sono stati scritti"
    args = ["-charset", "filename=utf8", "-charset", "iptc=utf8", "-codedcharacterset=utf8", "-overwrite_original",
            "-m", "-q", "-q", *tags, str(target)]
    return _run(exiftool, args, 60)


def copy_metadata(source: Path, target: Path, exiftool: Path | None = None, strip_gps: bool = False) -> str | None:
    """Copia i metadati di ``source`` in ``target`` (con ``strip_gps`` senza la posizione).
    Restituisce un messaggio d'errore o None."""
    exiftool = exiftool or find_exiftool()
    if exiftool is None:
        return "ExifTool non trovato: copiati solo i metadati essenziali"
    args = ["-charset", "filename=utf8", "-overwrite_original", "-m", "-q", "-q",
            "-TagsFromFile", str(source), "-all:all"]
    args += [f"--{tag}" for tag in _EXCLUDED]
    if strip_gps:
        args += ["--GPS:all", "--XMP-exif:GPS*"]
    args += [f"-Software=Denoiser {__version__}", str(target)]
    return _run(exiftool, args, 120)
