"""Elaborazione completa di un file (lettura -> denoise -> salvataggio), usata da CLI e GUI."""

from __future__ import annotations

import json
import os
import uuid

import numpy as np
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

from .algorithms import get_algorithm
from .bayer import denoised_sensor_data
from .develop import camera_raw_xmp, target_multipliers, wb_matrix, wb_ratio
from .image import Domain, ImageData
from .io import load_image, save_image
from .io.dng import neutral_from_multipliers, write_bayer_dng, write_linear_dng
from .io.formats import is_supported, kind_of
from .io.metadata import copy_metadata
from .io.naming import output_path, unique_path
from .pipeline import Analysis, ProgressFn, analyze, denoise_region, render, uses_sensor_stage
from .settings import Settings


@dataclass
class JobResult:
    source: Path
    output: Path | None = None
    error: str | None = None
    skipped: bool = False
    notes: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return self.error is None and not self.skipped


def collect_inputs(paths: Iterable[str | Path], recursive: bool = False, output_folder: str = "") -> list[Path]:
    """Espande le cartelle nei file supportati che contengono, senza duplicati.

    I file indicati esplicitamente vengono sempre inclusi, così un eventuale errore
    (file mancante o formato non supportato) viene segnalato invece di essere ignorato.
    Nelle cartelle si saltano i risultati di elaborazioni precedenti (la sottocartella di uscita,
    es. «denoised») e i file temporanei: rielaborarli creerebbe «denoised/denoised/…_dn_dn».
    """
    skip_dir = Path(output_folder).name if output_folder and not Path(output_folder).is_absolute() else ""
    absolute_out = Path(output_folder).resolve() if output_folder and Path(output_folder).is_absolute() else None

    def wanted(c: Path, root: Path) -> bool:
        if not c.is_file() or not is_supported(c) or ".parziale" in c.name:
            return False
        parents = c.relative_to(root).parts[:-1]
        if skip_dir and skip_dir in parents:
            return False
        return absolute_out is None or absolute_out not in c.resolve().parents

    found: list[Path] = []
    seen: set[Path] = set()
    for p in map(Path, paths):
        if p.is_dir():
            pattern = p.rglob("*") if recursive else p.glob("*")
            candidates = [c for c in sorted(pattern) if wanted(c, p)]
        else:
            candidates = [p]
        for c in candidates:
            key = c.resolve()
            if key not in seen:
                seen.add(key)
                found.append(c)
    return found


def settings_description(settings: Settings) -> str:
    """Testo registrato nel file di uscita per ricordare come è stata elaborata la foto."""
    return "Denoiser: " + json.dumps(
        {"denoise": settings.to_dict()["denoise"], "tone": settings.to_dict()["tone"]},
        ensure_ascii=False,
    )


def export_image(
    image: ImageData,
    analysis: Analysis,
    settings: Settings,
    destination: Path,
    progress: ProgressFn | None = None,
    notes: list[str] | None = None,
) -> Path:
    """Elabora l'intera foto, la salva nel formato scelto e copia i metadati dell'originale.

    Il file viene scritto con un nome temporaneo e rinominato solo alla fine: un'elaborazione
    interrotta (errore, annullamento, chiusura) non lascia mai un file a metà.
    """
    destination = Path(destination)
    if image.meta.source.is_file() and destination.resolve() == image.meta.source.resolve():
        raise ValueError("Il file di uscita coinciderebbe con l'originale: cambia cartella o nome in «Salvataggio».")
    # Nome temporaneo unico: due esportazioni verso lo stesso file non si pestano i piedi.
    temporary = destination.with_name(f"{destination.stem}.parziale-{uuid.uuid4().hex[:8]}{destination.suffix}")
    try:
        written = _write_output(image, analysis, settings, temporary, progress)
        if settings.output.copy_metadata and image.meta.source.is_file():
            if progress is not None:
                progress(1.0, "Copia dei metadati")
            problem = copy_metadata(image.meta.source, written, strip_gps=settings.output.strip_gps)
            if problem and notes is not None:
                notes.append(problem)
        if progress is not None:
            progress(1.0, "Salvataggio")  # ultimo punto in cui l'utente può ancora interrompere
        os.replace(written, destination)
    finally:
        if temporary.exists():
            temporary.unlink()
    return destination


def _write_output(
    image: ImageData, analysis: Analysis, settings: Settings, destination: Path, progress: ProgressFn | None
) -> Path:
    out = settings.output
    description = settings_description(settings)
    if out.format == "dng":
        return _export_dng(image, analysis, settings, destination, description, progress)
    pixels = render(image, analysis, settings, progress=progress)
    if out.max_side and max(pixels.shape[:2]) > out.max_side:
        import cv2

        h, w = pixels.shape[:2]
        scale = out.max_side / max(h, w)
        pixels = cv2.resize(pixels, (max(1, round(w * scale)), max(1, round(h * scale))), interpolation=cv2.INTER_AREA)
    return save_image(
        pixels,
        destination,
        out.format,
        bit_depth=out.bit_depth,
        jpeg_quality=out.jpeg_quality,
        alpha=image.alpha if out.format != "jpeg" else None,
        exif_bytes=image.meta.exif_bytes,
        description=description,
        icc_profile=image.meta.icc_profile,
    )


def _export_dng(
    image: ImageData,
    analysis: Analysis,
    settings: Settings,
    destination: Path,
    description: str,
    progress: ProgressFn | None,
) -> Path:
    if image.domain is not Domain.LINEAR:
        raise ValueError("Il formato DNG è disponibile solo per le foto RAW: scegli TIFF, PNG o JPEG.")
    make, model = image.meta.make, image.meta.model
    exposure = settings.tone.exposure_ev
    # Il RAW resta da sviluppare: toni e curva diventano istruzioni per Camera Raw, il bilanciamento
    # l'AsShotNeutral (o, nel DNG lineare, una correzione già applicata ai colori).
    xmp = camera_raw_xmp(settings.tone) if settings.output.camera_raw else None
    color = analysis.color
    if uses_sensor_stage(image, settings):
        # Livello 3: si salvano i dati del sensore ripuliti, il fotoritocco farà il resto.
        src = image.raw_source
        assert src is not None
        ds = settings.denoise
        active = ds.luma_strength > 0 or ds.chroma_strength > 0 or ds.hot_pixel_threshold > 0
        sensor = denoised_sensor_data(
            src.data, src.sensor_model, get_algorithm(ds.algorithm), ds, src.flip, denoise=active, progress=progress
        )
        if color is not None and settings.develop.wb_mode != "camera":
            sensor.camera_neutral = neutral_from_multipliers(
                target_multipliers(settings.develop, color, analysis.auto_wb))
        # L'esposizione di base del DNG d'origine (es. da Adobe DNG Converter) va conservata.
        return write_bayer_dng(destination, sensor, make, model, exposure + sensor.source_baseline_exposure,
                               description, xmp)
    linear = denoise_region(image, analysis, settings, progress=progress)
    if color is not None:
        matrix = wb_matrix(wb_ratio(settings.develop, color, analysis.auto_wb), color)
        if matrix is not None:
            linear = linear @ matrix.T.astype(np.float32)
    return write_linear_dng(destination, linear, make, model, exposure, description, xmp)


#: Cosa fare quando il file di uscita esiste già (esportazione di più foto).
EXISTING_POLICIES = {"rename": "Salva con un altro nome (_1, _2…)", "overwrite": "Sovrascrivi", "skip": "Salta la foto"}


def settings_for(path: Path, settings: Settings, non_raw_format: str) -> Settings:
    """Impostazioni per una foto di un'esportazione multipla.

    Il DNG si può creare solo da un RAW: se è stato scelto il DNG, JPG, PNG e TIFF vengono salvati
    nel formato ``non_raw_format`` scelto dall'utente per loro.
    """
    if settings.output.format != "dng" or kind_of(Path(path)) == "raw":
        return settings
    adjusted = Settings.from_dict(settings.to_dict())
    adjusted.output.format = non_raw_format
    return adjusted


def is_source(destination: Path, source: Path) -> bool:
    """True se ``destination`` è il file originale stesso (stesso nome, formato e cartella)."""
    try:
        return source.is_file() and destination.resolve() == source.resolve()
    except OSError:
        return False


def export_one(
    path: Path,
    settings: Settings,
    policy: str = "rename",
    converter=None,
    use_converter: bool = True,
    progress: ProgressFn | None = None,
    taken: frozenset[Path] = frozenset(),
    name_values: dict[str, str] | None = None,
    adjust=None,
) -> tuple[str, Path | None, list[str]]:
    """Esporta una foto di un'esportazione multipla.  ``name_values``: segnaposto in più per il nome. Restituisce (esito, file salvato, note); esito
    "ok" o "saltata".

    ``policy``: vedi :data:`EXISTING_POLICIES`. ``taken``: file già prodotti nella stessa esportazione
    (es. IMG_1.NEF e IMG_1.JPG danno lo stesso nome): non vengono mai sovrascritti né fanno saltare la
    foto, si usa un nome nuovo. Nemmeno l'originale viene mai sovrascritto, qualunque sia ``policy``.
    """
    image = load_image(path, settings.develop, use_converter, converter)
    destination = output_path(image.meta, settings.output, settings.denoise.algorithm, name_values)
    if destination in taken or is_source(destination, image.meta.source):
        destination = unique_path(destination)
    elif destination.exists():
        if policy == "skip":
            return "saltata", destination, []
        if policy == "rename":
            destination = unique_path(destination)
    notes = list(image.meta.notes)
    analysis = analyze(image)
    if adjust is not None:  # es. il preset suggerito dal rumore misurato, per le foto mai sviluppate
        settings = adjust(image, analysis, settings)
    saved = export_image(image, analysis, settings, destination, progress, notes)
    return "ok", saved, notes


def process_file(
    path: str | Path,
    settings: Settings,
    converter_path: str | Path | None = None,
    use_dng_converter: bool = True,
    progress: ProgressFn | None = None,
    image: ImageData | None = None,
    analysis: Analysis | None = None,
    taken: set[Path] | None = None,
) -> JobResult:
    """Elabora una foto. ``taken``: file già prodotti nella stessa corsa (es. IMG_1.NEF e IMG_1.JPG
    danno lo stesso nome): non vengono mai sovrascritti, si usa un nome nuovo; il nuovo file viene
    aggiunto all'insieme."""
    path = Path(path)
    result = JobResult(source=path)
    try:
        if image is None:
            image = load_image(path, settings.develop, use_dng_converter, converter_path)
        result.notes.extend(image.meta.notes)
        destination = output_path(image.meta, settings.output, settings.denoise.algorithm)
        if (taken is not None and destination in taken) or (destination.exists() and not settings.output.overwrite):
            destination = unique_path(destination)
        analysis = analysis or analyze(image)
        result.output = export_image(image, analysis, settings, destination, progress, result.notes)
        if taken is not None:
            taken.add(result.output)
    except Exception as exc:  # l'errore viene riportato all'utente, il batch prosegue
        result.error = str(exc) or exc.__class__.__name__
    return result
