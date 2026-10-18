"""Avvio dell'interfaccia grafica."""

from __future__ import annotations

import faulthandler
import os
import sys
import traceback
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication, QMessageBox

from .. import __version__
from .splash import StartupSplash
from .style import apply_theme

# Le librerie pesanti (numpy, OpenCV, LibRaw, ONNX…) e la finestra principale vengono importate dopo
# la comparsa della schermata di avvio, che ne mostra l'avanzamento.


def log_path() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    return Path(base) / "Denoiser" / "errori.log"


def _install_excepthook() -> None:
    """Un errore imprevisto non deve sparire in silenzio (nell'exe non c'è una console):
    lo si scrive in un file di log e lo si mostra all'utente."""

    def hook(exc_type, exc, tb) -> None:
        detail = "".join(traceback.format_exception(exc_type, exc, tb))
        try:
            path = log_path()
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8") as f:
                f.write(f"--- {datetime.now():%Y-%m-%d %H:%M:%S}\n{detail}\n")
        except OSError:
            path = None
        if sys.stderr is not None:
            sys.stderr.write(detail)
        if QApplication.instance() is not None:
            box = QMessageBox(QMessageBox.Icon.Warning, "Errore imprevisto",
                              "Si è verificato un errore imprevisto. Puoi continuare a lavorare; se si ripete, "
                              "salva il lavoro e riavvia l'app.")
            if path is not None:
                box.setInformativeText(f"I dettagli sono stati salvati in:\n{path}")
            box.setDetailedText(detail)
            box.exec()

    sys.excepthook = hook

    # Crash nativi (scheda video, driver, librerie in C): l'app si chiuderebbe senza lasciare traccia.
    # faulthandler scrive nel registro dove si trovava ogni thread in quel momento.
    try:
        path = log_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        global _CRASH_LOG
        _CRASH_LOG = path.open("a", encoding="utf-8")
        _CRASH_LOG.write(f"--- avvio {datetime.now():%Y-%m-%d %H:%M:%S}\n")
        _CRASH_LOG.flush()
        faulthandler.enable(file=_CRASH_LOG, all_threads=True)
    except OSError:
        pass


_CRASH_LOG = None  # file aperto per tutta la vita dell'app (faulthandler ne ha bisogno)


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv if argv is None else argv
    _install_excepthook()
    app = QApplication(argv)
    app.setApplicationName("Denoiser")
    app.setOrganizationName("Denoiser")
    apply_theme(app)

    splash = StartupSplash(__version__)
    splash.show()
    app.processEvents()
    splash.load_modules()

    from ..io.formats import is_supported
    from .main_window import MainWindow

    window = MainWindow()
    splash.show_progress(1.0, "Pronto")
    window.show()
    splash.finish(window)
    # Una foto passata da riga di comando (o trascinata sull'icona dell'eseguibile) viene aperta subito.
    files = [Path(a) for a in argv[1:] if Path(a).is_file() and is_supported(Path(a))]
    folders = [Path(a) for a in argv[1:] if Path(a).is_dir()]
    if files:
        window.open_path(files[0])
    elif folders:
        window.open_folder(folders[0])
    # Usato dalla build automatica per verificare che l'eseguibile si avvii.
    if os.environ.get("DENOISER_SMOKE_TEST"):
        QTimer.singleShot(1500, app.quit)
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
