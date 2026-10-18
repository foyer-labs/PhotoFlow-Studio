"""Riga di comando: `denoiser elabora`, `denoiser analizza`, `denoiser algoritmi`, `denoiser preset`."""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

from . import __version__
from .algorithms import ALGORITHMS, get_algorithm
from .batch import collect_inputs, process_file
from .io import UnsupportedImageError, load_image
from .io.naming import PLACEHOLDERS
from .pipeline import analyze, uses_sensor_stage
from .presets import list_presets, preset_path, resolve_preset, save_preset
from .settings import DEMOSAIC_ALGORITHMS, OUTPUT_FORMATS, WB_MODES, Settings


def _parse_param(text: str) -> tuple[str, str]:
    if "=" not in text:
        raise argparse.ArgumentTypeError(f"Atteso chiave=valore, ricevuto «{text}»")
    key, value = text.split("=", 1)
    return key.strip(), value.strip()


def _add_settings_args(p: argparse.ArgumentParser) -> None:
    g = p.add_argument_group("denoise")
    g.add_argument("--preset", help="nome di un preset o percorso di un file .json")
    g.add_argument("--algoritmo", choices=sorted(ALGORITHMS), help="algoritmo di denoise")
    g.add_argument("--luminanza", type=float, help="forza sulla luminanza (0 = disattivato, 1 = rumore stimato)")
    g.add_argument("--colore", type=float, help="forza sul colore")
    g.add_argument("--dettaglio", type=float, help="dettaglio originale reintrodotto, 0..1")
    g.add_argument("--pixel-caldi", type=float, help="soglia pixel caldi in multipli del rumore (0 = no)")
    g.add_argument(
        "--param", type=_parse_param, action="append", default=[], metavar="CHIAVE=VALORE",
        help="parametro specifico dell'algoritmo (vedi `denoiser algoritmi`)",
    )
    g = p.add_argument_group("RAW")
    g.add_argument("--bilanciamento", choices=sorted(WB_MODES), help="bilanciamento del bianco")
    g.add_argument("--moltiplicatori", type=float, nargs=3, metavar=("R", "G", "B"),
                   help="moltiplicatori per --bilanciamento custom")
    g.add_argument("--temperatura", type=float, metavar="K",
                   help="temperatura della luce in kelvin (implica --bilanciamento temperature)")
    g.add_argument("--tinta", type=float, help="tinta: − verso il verde, + verso il magenta (con --temperatura)")
    g.add_argument("--demosaic", choices=sorted(DEMOSAIC_ALGORITHMS), help="algoritmo di demosaicizzazione")
    g.add_argument(
        "--livello", choices=("3", "2"),
        help="3 = denoise sui dati del sensore prima della demosaicizzazione (predefinito, sensori Bayer); "
        "2 = denoise sull'immagine sviluppata",
    )
    g.add_argument("--esposizione", type=float, help="correzione di esposizione in EV")
    g.add_argument("--no-luminosita-auto", action="store_true", help="disattiva la luminosità automatica")
    g.add_argument("--dng-converter", help="percorso di Adobe DNG Converter")
    g.add_argument("--no-dng-converter", action="store_true", help="non usare Adobe DNG Converter")
    g = p.add_argument_group("uscita")
    g.add_argument("--formato", choices=sorted(OUTPUT_FORMATS), help="formato del file di uscita")
    g.add_argument("--bit", type=int, choices=(8, 16), help="profondità di bit (TIFF e PNG)")
    g.add_argument("--qualita", type=int, help="qualità JPEG 1..100")
    g.add_argument("--cartella", help="cartella di uscita (relativa alla foto o assoluta)")
    g.add_argument("--nome", help="modello del nome: " + ", ".join("{" + k + "}" for k in PLACEHOLDERS))
    g.add_argument("--sovrascrivi", action="store_true", help="sovrascrive i file esistenti")


def _settings_from_args(args: argparse.Namespace) -> Settings:
    s = resolve_preset(args.preset) if args.preset else Settings()
    d = s.denoise
    if args.algoritmo and args.algoritmo != d.algorithm:
        d.algorithm, d.params = args.algoritmo, {}
    for attr, value in (
        ("luma_strength", args.luminanza), ("chroma_strength", args.colore),
        ("detail", args.dettaglio), ("hot_pixel_threshold", args.pixel_caldi),
    ):
        if value is not None:
            setattr(d, attr, value)
    algorithm = get_algorithm(d.algorithm)
    known = {p.key for p in algorithm.params}
    for key, value in args.param:
        if key not in known:
            raise SystemExit(f"Parametro «{key}» non valido per {d.algorithm}. Validi: {', '.join(sorted(known))}")
        d.params[key] = value
    resolved = algorithm.resolve(d.params)
    for key, value in args.param:
        if str(resolved[key]) != str(value):
            print(f"Avviso: {key}={value} non ammesso, si usa {resolved[key]}", file=sys.stderr)
    d.params = resolved

    if args.bilanciamento:
        s.develop.wb_mode = args.bilanciamento
    if args.moltiplicatori:
        s.develop.wb_custom = tuple(args.moltiplicatori)
        if not args.bilanciamento:
            s.develop.wb_mode = "custom"  # indicare i moltiplicatori vale come scelta del bilanciamento
    if args.temperatura is not None or args.tinta is not None:
        if args.temperatura is not None:
            s.develop.temperature = min(max(args.temperatura, 2000.0), 15000.0)
        if args.tinta is not None:
            s.develop.tint = min(max(args.tinta, -150.0), 150.0)
        if not args.bilanciamento:
            s.develop.wb_mode = "temperature"
    if args.demosaic:
        s.develop.demosaic = args.demosaic
    if args.livello:
        s.denoise.raw_stage = "sensor" if args.livello == "3" else "rgb"
    if args.esposizione is not None:
        s.tone.exposure_ev = args.esposizione
    if args.no_luminosita_auto:
        s.tone.auto_brightness = False

    o = s.output
    if args.formato:
        o.format = args.formato
    if args.bit:
        o.bit_depth = args.bit
    if args.qualita is not None:
        o.jpeg_quality = max(1, min(100, args.qualita))
    if args.cartella is not None:
        o.folder = args.cartella
    if args.nome:
        o.name_template = args.nome
    if args.sovrascrivi:
        o.overwrite = True
    return s


def _cmd_elabora(args: argparse.Namespace) -> int:
    settings = _settings_from_args(args)
    files = collect_inputs(args.input, args.ricorsivo, settings.output.folder)
    if not files:
        print("Nessuna immagine trovata.", file=sys.stderr)
        return 1
    failures = 0
    produced: set[Path] = set()
    for i, f in enumerate(files, 1):
        start = time.perf_counter()
        print(f"[{i}/{len(files)}] {f.name} … ", end="", flush=True)
        res = process_file(
            f, settings, converter_path=args.dng_converter, use_dng_converter=not args.no_dng_converter,
            taken=produced,
        )
        if res.ok:
            print(f"ok ({time.perf_counter() - start:.1f} s) -> {res.output}")
            for note in res.notes:
                print(f"    nota: {note}")
        else:
            failures += 1
            print("ERRORE")
            print("    " + (res.error or "").replace("\n", "\n    "), file=sys.stderr)
    print(f"Completate {len(files) - failures} di {len(files)}.")
    return 0 if failures == 0 else 2


def _cmd_analizza(args: argparse.Namespace) -> int:
    settings = _settings_from_args(args)
    try:
        image = load_image(args.input, settings.develop, not args.no_dng_converter, args.dng_converter)
    except UnsupportedImageError as exc:
        print(exc, file=sys.stderr)
        return 1
    m = image.meta
    print(f"File:        {m.source}")
    print(f"Tipo:        {m.kind} ({image.domain.value}), {image.width}x{image.height}, {m.bits_per_sample} bit")
    if m.camera:
        print(f"Fotocamera:  {m.camera}")
    if m.iso:
        print(f"ISO:         {m.iso:g}")
    if m.wb_multipliers:
        print("Bil. bianco: R {:.3f}  G {:.3f}  B {:.3f}".format(*m.wb_multipliers))
    if image.domain.value == "linear":
        print(f"Sensore:     {'Bayer ' + m.cfa_pattern if m.cfa_pattern else 'non Bayer (es. X-Trans)'}")
        level = "3 (dati del sensore)" if uses_sensor_stage(image, settings) else "2 (immagine sviluppata)"
        print(f"Denoise:     livello {level}")
    for note in m.notes:
        print(f"Nota:        {note}")
    analysis = analyze(image)
    profile = image.noise_profile
    if profile is not None and profile.sensor_model is not None:
        sm = profile.sensor_model
        for c, name in enumerate("RGB"):
            print(f"Rumore sensore {name}: var = {sm.a[c]:.3g}·x + {sm.b[c]:.3g}")
    print(analysis.describe())
    return 0


def _cmd_algoritmi(_: argparse.Namespace) -> int:
    for alg in ALGORITHMS.values():
        print(f"{alg.key} — {alg.label}\n  {alg.description}")
        for p in alg.params:
            extra = ""
            if p.choices:
                extra = " valori: " + ", ".join(p.choices)
            elif p.minimum is not None:
                extra = f" intervallo: {p.minimum:g}..{p.maximum:g}"
            print(f"    {p.key} (predefinito {p.default}){extra}\n      {p.tooltip}")
        print()
    return 0


def _cmd_preset(args: argparse.Namespace) -> int:
    if args.azione == "elenca":
        for name, (description, value) in list_presets().items():
            origin = "predefinito" if not isinstance(value, Path) else str(value)
            print(f"{name}  ({origin})\n    {description}")
        return 0
    settings = _settings_from_args(args)
    path = save_preset(settings, preset_path(args.nome_preset))
    print(f"Preset salvato in {path}")
    return 0


def _cmd_modelli(args: argparse.Namespace) -> int:
    from .neural.models import MODELS, download, is_downloaded, models_dir
    from .neural.runtime import describe_devices

    if args.azione == "elenca":
        print(f"Cartella dei modelli: {models_dir()}")
        print(describe_devices())
        for spec in MODELS.values():
            state = "scaricato" if is_downloaded(spec) else f"da scaricare ({spec.size_mb} MB)"
            print(f"\n{spec.key} — {spec.label}: {state}\n  {spec.description}\n  Licenza: {spec.license}")
        return 0
    keys = list(MODELS) if args.modello in (None, "tutti") else [args.modello]
    for key in keys:
        if key not in MODELS:
            print(f"Modello sconosciuto: {key}. Disponibili: {', '.join(MODELS)}", file=sys.stderr)
            return 1
        spec = MODELS[key]

        def progress(fraction: float, message: str) -> None:
            print(f"\r{message}   ", end="", flush=True)

        try:
            path = download(spec, progress)
        except Exception as exc:
            print(f"\nDownload di {spec.label} non riuscito: {exc}", file=sys.stderr)
            return 1
        print(f"\n{spec.label}: {path}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="denoiser", description="Riduzione del rumore per foto RAW, JPG e PNG.")
    parser.add_argument("--version", action="version", version=f"denoiser {__version__}")
    sub = parser.add_subparsers(dest="comando", required=True)

    p = sub.add_parser("elabora", help="elabora file o cartelle")
    p.add_argument("input", nargs="+", help="file o cartelle da elaborare")
    p.add_argument("--ricorsivo", action="store_true", help="include le sottocartelle")
    _add_settings_args(p)
    p.set_defaults(func=_cmd_elabora)

    p = sub.add_parser("analizza", help="mostra informazioni e rumore stimato di una foto")
    p.add_argument("input")
    _add_settings_args(p)
    p.set_defaults(func=_cmd_analizza)

    p = sub.add_parser("algoritmi", help="elenca algoritmi e parametri")
    p.set_defaults(func=_cmd_algoritmi)

    p = sub.add_parser("modelli", help="elenca o scarica i modelli neurali")
    p.add_argument("azione", choices=("elenca", "scarica"))
    p.add_argument("modello", nargs="?", help="drunet, scunet, restormer, rawnind o tutti (predefinito)")
    p.set_defaults(func=_cmd_modelli)

    p = sub.add_parser("preset", help="elenca o salva preset")
    p.add_argument("azione", choices=("elenca", "salva"))
    p.add_argument("nome_preset", nargs="?", help="nome del preset da salvare")
    _add_settings_args(p)
    p.set_defaults(func=_cmd_preset)
    return parser


def main(argv: list[str] | None = None) -> int:
    # Su Windows l'output reindirizzato usa la codepage locale: i caratteri non
    # rappresentabili vengono sostituiti invece di interrompere il programma.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = build_parser()
    args = parser.parse_args(argv)
    if getattr(args, "azione", None) == "salva" and not args.nome_preset:
        parser.error("indica il nome del preset da salvare")
    try:
        return args.func(args)
    except ValueError as exc:
        print(f"Errore: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
