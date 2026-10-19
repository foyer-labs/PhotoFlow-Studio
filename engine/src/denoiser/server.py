"""Motore di Denoiser per altri programmi (PhotoFlow Studio).

Protocollo: una richiesta JSON per riga su stdin, una risposta JSON per riga su stdout.

- richiesta: ``{"id": 7, "cmd": "render", ...}``
- risposta: ``{"id": 7, "ok": true, "result": {...}}`` oppure ``{"id": 7, "ok": false, "error": "...",
  "detail": "..."}``
- avanzamento (lavori lunghi): ``{"id": 7, "event": "progress", "fraction": 0.4, "message": "..."}``
- annullamento: ``{"cmd": "cancel", "target": 7}`` (senza risposta; il lavoro risponde «cancelled»)

Le richieste brevi (aprire, anteprime, regolazioni) e quelle lunghe (esportazione, download dei modelli)
girano in due code separate: un'esportazione non blocca le anteprime. Tutto ciò che le librerie
scrivono su stdout finisce su stderr, così il canale del protocollo resta pulito.
"""

from __future__ import annotations

import json
import os
import queue
import sys
import threading
import traceback
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

import cv2
import numpy as np

from . import __version__
from . import geometry
from .algorithms import ALGORITHMS, get_algorithm
from .batch import EXISTING_POLICIES, export_one, settings_for
from .color import srgb_decode
from .develop import ColorInfo, histogram, render_tone, temperature_from_multipliers
from .image import ImageData
from .io import load_image
from .io.dng_converter import find_converter
from .pipeline import Analysis, analyze, denoise_region, noise_judgement, uses_sensor_stage
from .presets import PresetError, apply_preset, list_presets, preset_content
from .settings import DEMOSAIC_ALGORITHMS, OUTPUT_FORMATS, RAW_STAGES, WB_MODES, Settings
from .sidecar import apply_photo_settings, changed_fields, copy_fields, has_settings, read_photo_settings, write_photo_settings

#: Pixel della copia ridotta per istogramma e anteprima «adatta» veloce.
_PROXY_PIXELS = 1_500_000
#: Comandi lunghi: coda separata, con avanzamento e annullamento.
_LONG = {"export", "download_model", "render_full"}
#: Preparazione in background delle foto non aperte (M8): una coda sua, per non far aspettare la foto aperta.
_BACKGROUND = {"prepare"}


class Cancelled(Exception):
    pass


@dataclass
class _Photo:
    image: ImageData
    analysis: Analysis
    proxy: np.ndarray
    #: Denoise già calcolati: (chiave delle impostazioni, zona) -> RGB lineare.
    denoised: dict[tuple[str, tuple], np.ndarray] = field(default_factory=dict)


def _settings(data: dict | None) -> Settings:
    return Settings.from_dict(data or {})


def _denoise_key(settings: Settings, develop_demosaic: str) -> str:
    return json.dumps({"d": settings.to_dict()["denoise"], "m": develop_demosaic}, sort_keys=True)


def _resize(pixels: np.ndarray, max_pixels: int) -> np.ndarray:
    h, w = pixels.shape[:2]
    scale = min(1.0, (max_pixels / max(h * w, 1)) ** 0.5)
    if scale >= 1.0:
        return pixels
    return cv2.resize(pixels, (max(1, int(w * scale)), max(1, int(h * scale))), interpolation=cv2.INTER_AREA)


def _fit(pixels: np.ndarray, max_side: int) -> np.ndarray:
    h, w = pixels.shape[:2]
    scale = min(1.0, max_side / max(h, w))
    if scale >= 1.0:
        return pixels
    return cv2.resize(pixels, (max(1, round(w * scale)), max(1, round(h * scale))), interpolation=cv2.INTER_AREA)


def _write_png(rgb: np.ndarray, folder: Path, tag: str) -> str:
    u8 = rgb if rgb.dtype == np.uint8 else (np.clip(rgb, 0.0, 1.0) * 255 + 0.5).astype(np.uint8)
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{uuid.uuid4().hex[:12]}-{tag}.png"
    ok, data = cv2.imencode(".png", cv2.cvtColor(np.ascontiguousarray(u8), cv2.COLOR_RGB2BGR),
                            [cv2.IMWRITE_PNG_COMPRESSION, 1])
    if not ok:
        raise RuntimeError("Codifica PNG non riuscita")
    path.write_bytes(data.tobytes())
    return str(path)


def _luma(rgb: np.ndarray) -> np.ndarray:
    return rgb.astype(np.float32) @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)


#: Preset proposto dal rumore misurato (PhotoFlow Studio, decisione S8): metodi classici, veloci e sempre
#: disponibili; il metodo neurale resta una scelta attiva dell'utente.
_PRESET_BY_NOISE = {"basso": "Delicato", "medio": "Bilanciato", "alto": "Forte (ISO alti)", "molto alto": "Forte (ISO alti)"}


def suggest_preset(noise_level: str, is_raw: bool) -> str:
    name = _PRESET_BY_NOISE.get(noise_level, "Bilanciato")
    return name if name in list_presets() else "Bilanciato"


def _auto_preset(image, analysis, settings: Settings) -> Settings:
    """Foto mai sviluppata: il preset proposto dal rumore misurato (stessa scelta di «open»)."""
    level, _detail = noise_judgement(image, analysis)
    name = suggest_preset(level, image.domain.value == "linear")
    try:
        adjusted = apply_preset(settings, preset_content(list_presets()[name][1]))
    except (KeyError, PresetError):
        return settings
    adjusted.output = settings.output
    return adjusted


class Engine:
    """Esegue i comandi. Indipendente dal canale: i test lo usano direttamente."""

    def __init__(self, temp_dir: str | Path | None = None):
        self._photos: dict[str, _Photo] = {}
        self._lock = threading.Lock()
        self.temp_dir = Path(temp_dir) if temp_dir else Path(os.environ.get("TEMP", ".")) / "photoflow-studio-engine"

    # ------------------------------------------------------------------ informazioni

    def cmd_hello(self, **_: Any) -> dict:
        from .neural.runtime import gpu_status

        has_gpu, gpu_text = gpu_status()
        algorithms = []
        for key, alg in ALGORITHMS.items():
            spec = getattr(alg, "spec", None) if alg.joint else None
            algorithms.append({
                "key": key, "label": alg.label, "summary": alg.summary, "description": alg.description,
                "joint": alg.joint, "blind": alg.blind, "available": alg.is_available(),
                "size_mb": spec.size_mb if spec else 0, "license": spec.license if spec else "",
                "params": [{"key": p.key, "label": p.label, "default": p.default} for p in alg.params],
            })
        presets = [{"name": n, "description": d} for n, (d, _e) in list_presets().items()]
        return {
            "version": __version__, "algorithms": algorithms, "presets": presets,
            "gpu": {"available": has_gpu, "text": gpu_text}, "defaults": Settings().to_dict(),
            "choices": {"wb_modes": WB_MODES, "demosaic": DEMOSAIC_ALGORITHMS, "raw_stages": RAW_STAGES,
                        "output_formats": OUTPUT_FORMATS, "policies": EXISTING_POLICIES},
        }

    # ------------------------------------------------------------------ foto

    def _photo(self, path: str) -> _Photo:
        with self._lock:
            photo = self._photos.get(path)
        if photo is None:
            raise KeyError(f"Foto non aperta: {path}")
        return photo

    def cmd_open(self, path: str, base: dict | None = None, use_dng_converter: bool = True,
                 converter: str | None = None, **_: Any) -> dict:
        """Apre la foto (sviluppo con la ricostruzione dei colori delle sue regolazioni) e ne dà le
        informazioni e le regolazioni: quelle salvate per la foto, altrimenti ``base``."""
        settings = apply_photo_settings(_settings(base), read_photo_settings(path))
        conv = find_converter(converter) if use_dng_converter else None
        image = load_image(path, settings.develop, use_dng_converter, conv)
        analysis = analyze(image)
        photo = _Photo(image, analysis, _resize(image.pixels, _PROXY_PIXELS))
        with self._lock:
            self._photos[path] = photo
        level, detail = noise_judgement(image, analysis)
        m = image.meta
        return {
            "width": image.width, "height": image.height, "is_raw": image.domain.value == "linear",
            "is_bayer": image.raw_source is not None, "camera": m.camera, "iso": m.iso, "date": m.date,
            "bits": m.bits_per_sample, "notes": list(m.notes), "noise": {"level": level, "detail": detail},
            "sensor_stage": uses_sensor_stage(image, settings), "wb_equivalents": self._wb_equivalents(analysis),
            "settings": settings.to_dict(), "has_settings": has_settings(path),
            "suggested_preset": suggest_preset(level, image.domain.value == "linear"),
        }

    @staticmethod
    def _wb_equivalents(analysis: Analysis) -> dict:
        color = analysis.color
        if color is None:
            return {}
        result = {"camera": list(color.as_shot())}
        if analysis.auto_wb is not None:
            result["auto"] = list(temperature_from_multipliers(color.base * analysis.auto_wb, color.cam_from_xyz))
        if color.daylight is not None:
            result["daylight"] = list(temperature_from_multipliers(color.daylight, color.cam_from_xyz))
        return result

    def cmd_close(self, path: str, **_: Any) -> dict:
        with self._lock:
            self._photos.pop(path, None)
        return {}

    def _tone(self, photo: _Photo, linear: np.ndarray, settings: Settings) -> np.ndarray:
        return render_tone(linear, photo.image.domain.value == "linear", photo.analysis.tone_params(),
                           settings.develop, settings.tone)

    def cmd_histogram(self, path: str, settings: dict, **_: Any) -> dict:
        photo = self._photo(path)
        s = _settings(settings)
        rgb = geometry.apply(self._tone(photo, photo.proxy, s), s.geometry)
        return {"histogram": histogram(rgb).tolist()}

    def cmd_frame(self, path: str, settings: dict, **_: Any) -> dict:
        """Dimensione dell'uscita (dopo rotazione e ritaglio) a piena risoluzione."""
        image = self._photo(path).image
        w, h = geometry.output_size(image.width, image.height, _settings(settings).geometry)
        return {"frame": [w, h]}

    def cmd_auto_level(self, path: str, settings: dict, **_: Any) -> dict:
        """Raddrizzamento automatico: l'angolo che rende orizzontali o verticali le linee lunghe."""
        photo = self._photo(path)
        s = _settings(settings)
        s.geometry.angle, s.geometry.crop = 0.0, geometry.FULL_CROP
        rgb = geometry.apply(self._tone(photo, photo.proxy, s), s.geometry)
        return {"angle": geometry.level_angle(rgb)}

    def cmd_render(self, path: str, settings: dict, region: list[int] | None = None, max_side: int = 2048,
                   denoise: bool = False, progress: Callable | None = None, **_: Any) -> dict:
        """Anteprima: «prima» (solo sviluppo) e, se ``denoise``, «dopo» (sviluppo e riduzione del rumore).

        Senza ``region``: tutta la foto ridotta a ``max_side`` (il denoise, se richiesto, è sulla foto
        intera: usare la coda lunga «render_full»). Con ``region`` [x, y, w, h]: la zona a piena risoluzione,
        ridotta a ``max_side`` se è positivo e la zona è più grande (zoom sotto il 100%, PhotoFlow Studio S25).
        Le coordinate e la dimensione sono quelle dell'uscita, dopo rotazione e ritaglio (``frame``).
        """
        photo = self._photo(path)
        s = _settings(settings)
        image = photo.image
        g = s.geometry
        frame = geometry.output_size(image.width, image.height, g)
        if region is None:
            # Lato della foto sviluppata perché l'uscita ritagliata arrivi a max_side.
            side = max(image.width, image.height) * min(1.0, max_side / max(frame))
            side = max(1, int(round(side)))
            out_rect = (0, 0, frame[0], frame[1])
            rect = (0, 0, image.width, image.height)
            source = photo.proxy if max(photo.proxy.shape[:2]) >= side else image.pixels
            before_lin = _fit(source, side)
            shape = lambda rgb: geometry.apply(rgb, g)  # noqa: E731
        else:
            out_rect = geometry.clamp_region(region, image.width, image.height, g)
            rect = geometry.source_box(out_rect, image.width, image.height, g) if not geometry.is_identity(g) else out_rect
            before_lin = image.pixels[rect[1]:rect[1] + rect[3], rect[0]:rect[0] + rect[2]]
            if geometry.is_identity(g):
                shape = lambda rgb: rgb  # noqa: E731
            else:
                shape = lambda rgb: geometry.apply_region(rgb, rect, out_rect, image.width, image.height, g)  # noqa: E731
        if region is not None and max_side > 0:
            sized = shape
            shape = lambda rgb: _fit(sized(rgb), max_side)  # noqa: E731
        before = shape(self._tone(photo, before_lin, s))
        result = {"region": list(out_rect), "frame": list(frame), "before": _write_png(before, self.temp_dir, "prima"),
                  "width": before.shape[1], "height": before.shape[0]}
        if denoise:
            key = (_denoise_key(s, s.develop.demosaic), rect)
            linear = photo.denoised.get(key)
            if linear is None:
                linear = denoise_region(image, photo.analysis, s, rect, True, progress)
                photo.denoised = {k: v for k, v in photo.denoised.items() if k[0] == key[0]}  # solo l'ultimo metodo
                photo.denoised[key] = linear
            after_lin = _fit(linear, max(before_lin.shape[:2])) if region is None else linear
            after = shape(self._tone(photo, after_lin, s))
            result["after"] = _write_png(after, self.temp_dir, "dopo")
            diff = cv2.addWeighted((after * 255).astype(np.uint8), 4.0, (before * 255).astype(np.uint8), -4.0, 127.5) \
                if after.shape == before.shape else None
            if diff is not None:
                result["difference"] = _write_png(diff, self.temp_dir, "differenza")
            try:
                s_before = float(np.std(np.diff(_luma(before), axis=1)))
                s_after = float(np.std(np.diff(_luma(after), axis=1)))
                if s_before > 1e-4:
                    result["reduction"] = round(min(max(0.0, 1 - s_after / s_before) * 100, 99.0))
            except (ValueError, FloatingPointError):
                pass
        return result

    def cmd_render_full(self, **kw: Any) -> dict:
        """Anteprima della foto intera con il denoise (coda lunga, annullabile)."""
        return self.cmd_render(**{**kw, "region": None, "denoise": True})

    def cmd_prepare(self, path: str, settings: dict | None = None, max_side: int = 2048, auto: bool = False,
                    use_dng_converter: bool = True, converter: str | None = None, progress: Callable | None = None,
                    **_: Any) -> dict:
        """Anteprima della foto intera ripulita di una foto non aperta (coda lunga, PhotoFlow Studio M8): la apre,
        la calcola come «render_full» e la chiude, senza toccare le foto aperte. ``auto``: foto mai sviluppata, si
        usa il preset suggerito dal rumore misurato. Restituisce anche le impostazioni usate."""
        s = _settings(settings)
        conv = find_converter(converter) if use_dng_converter else None
        image = load_image(path, s.develop, use_dng_converter, conv)
        analysis = analyze(image)
        if auto:
            s = _auto_preset(image, analysis, s)
        photo = _Photo(image, analysis, _resize(image.pixels, _PROXY_PIXELS))
        g = s.geometry
        frame = geometry.output_size(image.width, image.height, g)
        side = max(1, int(round(max(image.width, image.height) * min(1.0, max_side / max(frame)))))
        before = geometry.apply(self._tone(photo, _fit(image.pixels, side), s), g)
        linear = denoise_region(image, analysis, s, None, True, progress)
        after = geometry.apply(self._tone(photo, _fit(linear, side), s), g)
        result = {"region": [0, 0, frame[0], frame[1]], "frame": list(frame), "width": after.shape[1], "height": after.shape[0],
                  "before": _write_png(before, self.temp_dir, "prima"), "after": _write_png(after, self.temp_dir, "dopo"),
                  "settings": s.to_dict()}
        if after.shape == before.shape:
            diff = cv2.addWeighted((after * 255).astype(np.uint8), 4.0, (before * 255).astype(np.uint8), -4.0, 127.5)
            result["difference"] = _write_png(diff, self.temp_dir, "differenza")
            s_before = float(np.std(np.diff(_luma(before), axis=1)))
            if s_before > 1e-4:
                result["reduction"] = round(min(max(0.0, 1 - float(np.std(np.diff(_luma(after), axis=1))) / s_before) * 100, 99.0))
        return result

    def cmd_pick_wb(self, path: str, x: float, y: float, settings: dict | None = None, **_: Any) -> dict:
        """Contagocce: temperatura e tinta che rendono neutro il punto (media di un quadratino).

        ``x``, ``y``: punto dell'uscita (dopo rotazione e ritaglio di ``settings``) a piena risoluzione."""
        photo = self._photo(path)
        image = photo.image
        if settings:
            x, y = geometry.to_source(x, y, image.width, image.height, _settings(settings).geometry)
        r = max(2, int(min(image.width, image.height) / 400))
        xi, yi = int(x), int(y)
        patch = image.pixels[max(0, yi - r): yi + r + 1, max(0, xi - r): xi + r + 1].reshape(-1, 3)
        if image.domain.value != "linear":
            patch = srgb_decode(patch)
        if patch.size == 0:
            raise ValueError("Punto fuori dalla foto")
        mean = patch.mean(axis=0)
        if mean.max() >= 0.98 or mean.min() <= 1e-4:
            raise ValueError("Quel punto è bruciato o troppo scuro: scegline uno grigio o bianco con un po' di dettaglio.")
        color = photo.analysis.color or ColorInfo.for_display()
        t, tint = temperature_from_multipliers(color.base * color.gray_world(mean), color.cam_from_xyz)
        return {"temperature": round(t), "tint": round(tint)}

    # ------------------------------------------------------------------ regolazioni di ogni foto

    def cmd_load_settings(self, path: str, base: dict | None = None, **_: Any) -> dict:
        settings = apply_photo_settings(_settings(base), read_photo_settings(path))
        return {"settings": settings.to_dict(), "has_settings": has_settings(path)}

    def cmd_save_settings(self, path: str, settings: dict, **_: Any) -> dict:
        return {"file": str(write_photo_settings(path, _settings(settings)))}

    def cmd_sync_settings(self, before: dict, after: dict, targets: list[str], base: dict | None = None,
                          **_: Any) -> dict:
        """Copia sulle foto ``targets`` solo i campi cambiati tra ``before`` e ``after`` (raffiche)."""
        b, a = _settings(before), _settings(after)
        fields = changed_fields(b.to_dict(), a.to_dict())
        written, failed = [], []
        for target in targets:
            current = apply_photo_settings(_settings(base), read_photo_settings(target))
            try:
                write_photo_settings(target, copy_fields(current, a, fields))
                written.append(target)
            except OSError as exc:
                failed.append({"path": target, "error": str(exc)})
        return {"fields": [list(f) for f in fields], "written": written, "failed": failed}

    def cmd_apply_preset(self, name: str, settings: dict, **_: Any) -> dict:
        try:
            content = preset_content(list_presets()[name][1])
        except (KeyError, PresetError) as exc:
            raise ValueError(f"Preset non disponibile: {name}") from exc
        return {"settings": apply_preset(_settings(settings), content).to_dict()}

    # ------------------------------------------------------------------ lavori lunghi

    def cmd_export(self, items: list[dict], output: dict, policy: str = "rename", non_raw_format: str = "tiff",
                   algorithm: str | None = None, use_dng_converter: bool = True, converter: str | None = None,
                   progress: Callable | None = None, **_: Any) -> dict:
        """Esporta le foto ``items`` [{"path", "settings"}], ognuna con le sue regolazioni."""
        conv = find_converter(converter) if use_dng_converter else None
        results, taken = [], []
        total = max(len(items), 1)
        for index, item in enumerate(items):
            path = item["path"]
            s = _settings(item.get("settings"))
            s.output = Settings.from_dict({"output": output}).output
            if algorithm and s.denoise.algorithm != algorithm:
                s.denoise.algorithm, s.denoise.params = algorithm, {}
            s = settings_for(Path(path), s, non_raw_format)

            def step(fraction: float, message: str, i: int = index, name: str = Path(path).name) -> None:
                if progress is not None:
                    progress((i + fraction) / total, f"{name} ({i + 1} di {len(items)}): {message}")

            try:
                adjust = _auto_preset if item.get("auto") else None
                outcome, saved, notes = export_one(Path(path), s, policy, conv, use_dng_converter, step,
                                                   frozenset(taken), item.get("name_values"), adjust, item.get("metadata"))
                if saved is not None and outcome == "ok":
                    taken.append(saved)
                results.append({"path": path, "outcome": outcome, "output": str(saved) if saved else None,
                                "notes": notes})
            except Cancelled:
                raise
            except Exception as exc:  # una foto con errori non ferma le altre
                results.append({"path": path, "outcome": "errore", "output": None, "notes": [str(exc)]})
        return {"results": results}

    def cmd_download_model(self, key: str, progress: Callable | None = None, **_: Any) -> dict:
        from .neural.models import MODELS, download

        download(MODELS[key], progress)
        return {"available": get_algorithm(key).is_available()}


# ====================================================================== canale


class Server:
    def __init__(self, engine: Engine, stdin, stdout):
        self.engine = engine
        self._stdin = stdin
        self._stdout = stdout
        self._write_lock = threading.Lock()
        self._cancelled: set[int] = set()
        self._queues = {"short": queue.Queue(), "long": queue.Queue(), "background": queue.Queue()}

    def send(self, message: dict) -> None:
        line = json.dumps(message, ensure_ascii=False) + "\n"
        with self._write_lock:
            self._stdout.write(line)
            self._stdout.flush()

    def _worker(self, lane: str) -> None:
        while True:
            request = self._queues[lane].get()
            if request is None:
                return
            self._handle(request)

    def _handle(self, request: dict) -> None:
        rid = request.get("id")
        cmd = request.get("cmd", "")
        handler = getattr(self.engine, f"cmd_{cmd}", None)
        if handler is None:
            self.send({"id": rid, "ok": False, "error": f"Comando sconosciuto: {cmd}"})
            return

        def progress(fraction: float, message: str) -> None:
            if rid in self._cancelled:
                raise Cancelled()
            self.send({"id": rid, "event": "progress", "fraction": round(float(fraction), 4), "message": message})

        params = {k: v for k, v in request.items() if k not in ("id", "cmd")}
        try:
            if rid in self._cancelled:
                raise Cancelled()
            result = handler(progress=progress, **params)
            self.send({"id": rid, "ok": True, "result": result})
        except Cancelled:
            self.send({"id": rid, "ok": False, "cancelled": True, "error": "Interrotto"})
        except Exception as exc:  # comunicato al chiamante
            self.send({"id": rid, "ok": False, "error": str(exc) or exc.__class__.__name__,
                       "detail": traceback.format_exc()})
        finally:
            self._cancelled.discard(rid)

    def run(self) -> None:
        workers = [threading.Thread(target=self._worker, args=(lane,), daemon=True) for lane in self._queues]
        for t in workers:
            t.start()
        for line in self._stdin:
            line = line.strip()
            if not line:
                continue
            try:
                request = json.loads(line)
            except ValueError:
                self.send({"id": None, "ok": False, "error": "Richiesta non valida"})
                continue
            cmd = request.get("cmd")
            if cmd == "cancel":
                self._cancelled.add(request.get("target"))
                continue
            if cmd == "quit":
                break
            lane = "background" if cmd in _BACKGROUND else "long" if cmd in _LONG else "short"
            self._queues[lane].put(request)
        for q in self._queues.values():
            q.put(None)


def main() -> int:
    """Avvia il motore. stdout del protocollo: quello originale; tutto il resto va su stderr."""
    protocol_out = open(sys.stdout.fileno(), "w", encoding="utf-8", newline="\n", closefd=False)
    sys.stdout = sys.stderr
    protocol_in = open(sys.stdin.fileno(), "r", encoding="utf-8", closefd=False)
    temp = os.environ.get("PHOTOFLOW_STUDIO_TEMP")
    server = Server(Engine(temp), protocol_in, protocol_out)
    server.send({"id": 0, "event": "ready", "version": __version__})
    server.run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
