"""Finestra principale dell'applicazione."""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

import cv2
import numpy as np
from PySide6.QtCore import QEvent, QSettings, Qt, QThreadPool, QTimer, QUrl
from PySide6.QtGui import QAction, QActionGroup, QDesktopServices, QFontMetrics, QKeySequence
from PySide6.QtWidgets import (
    QApplication,
    QCheckBox,
    QDialog,
    QDialogButtonBox,
    QDockWidget,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QInputDialog,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMenu,
    QMessageBox,
    QProgressBar,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QStackedWidget,
    QToolBar,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from .. import __version__
from ..batch import collect_inputs, export_image, export_one, is_source, settings_for
from ..image import ImageData, ImageMeta
from ..io import load_image
from ..io.dng_converter import find_converter
from ..algorithms import get_algorithm
from ..color import srgb_decode
from ..develop import ColorInfo, histogram, render_tone, temperature_from_multipliers
from ..io.formats import file_dialog_filter, is_supported, kind_of
from ..io.naming import output_path, unique_path
from ..noise import estimate_sigma, sample_tiles
from ..pipeline import Analysis, analyze, denoise_region, noise_judgement, render, uses_sensor_stage
from ..presets import (
    BUILTIN_PRESETS,
    SECTIONS,
    PresetError,
    apply_preset,
    list_presets,
    preset_content,
    preset_path,
    save_preset,
    user_presets_dir,
)
from ..settings import Settings
from ..sidecar import (
    SECTIONS as PHOTO_SECTIONS,
    apply_photo_settings,
    changed_fields,
    copy_fields,
    read_photo_settings,
    write_photo_settings,
)
from .export_dialog import ExportChoice, ExportDialog
from .filmstrip import Filmstrip
from .settings_panel import SettingsPanel
from .split_view import MODES, SplitView, build_pyramid, to_uint8
from .style import OK, WARN, app_icon, dark_title_bar, icon
from .widgets import ActionButton, KeyBar, shortcut_text
from .workers import EtaEstimator, Task, format_duration, start

#: Oltre questa area visibile non si elabora la sola zona ma la foto intera (in background): il
#: risultato vale poi per qualsiasi ingrandimento e spostamento, senza ricalcoli.
PREVIEW_MAX_PIXELS = 3_000_000
#: Righe elaborate alla volta nella resa tonale dell'anteprima (limita la memoria sulle foto grandi).
_TONE_ROWS = 512
_MAX_RECENT = 10
_DROP_TEXT = "Trascina qui una foto o una cartella, oppure"
_APPLY_HINT = ("Scegli il metodo nel pannello a destra, poi fai clic qui o premi «Applica» (Ctrl+Invio) "
               "per ridurre il rumore")


def _luma(rgb: np.ndarray) -> np.ndarray:
    return (rgb @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)).astype(np.float32)


def contains(outer, inner) -> bool:
    """True se il rettangolo (x, y, w, h) ``outer`` contiene ``inner``."""
    ox, oy, ow, oh = outer
    ix, iy, iw, ih = inner
    return ox <= ix and oy <= iy and ix + iw <= ox + ow and iy + ih <= oy + oh


#: Pixel della copia ridotta della foto usata per l'istogramma (calcolato a ogni regolazione).
_PROXY_PIXELS = 400_000


def raw_develop(settings: dict) -> dict:
    """La parte dello sviluppo che richiede di riaprire il RAW con LibRaw (il resto è istantaneo)."""
    return {"demosaic": settings["develop"]["demosaic"]}


def make_proxy(pixels: np.ndarray) -> np.ndarray:
    h, w = pixels.shape[:2]
    scale = min(1.0, (_PROXY_PIXELS / max(h * w, 1)) ** 0.5)
    if scale >= 1.0:
        return pixels.copy()
    return cv2.resize(pixels, (max(1, int(w * scale)), max(1, int(h * scale))), interpolation=cv2.INTER_AREA)


def base_job(image: ImageData, analysis: Analysis, settings: Settings):
    """Immagine «prima» (senza denoise) già ridotta nei livelli per la vista."""
    return build_pyramid(render(image, analysis, settings, None, False))


def preview_job(image: ImageData, analysis: Analysis, settings: Settings, rect, before: np.ndarray, progress=None):
    """Lavoro dell'anteprima: denoise della zona, resa tonale, vista del rumore tolto e statistiche."""
    linear = denoise_region(image, analysis, settings, rect, True, progress)
    return finish_preview(image, analysis, settings, linear, before)


def finish_preview(image: ImageData, analysis: Analysis, settings: Settings, linear: np.ndarray, before: np.ndarray):
    """Resa tonale del risultato, vista del rumore tolto e statistiche.

    ``before``: la stessa zona dell'immagine «prima» in uint8. Si lavora a strisce e in uint8, così
    anche il risultato sulla foto intera (decine di megapixel) non moltiplica la memoria occupata.
    """
    from ..pipeline import _tone

    h, w = linear.shape[:2]
    rgb = np.empty((h, w, 3), np.uint8)
    diff = np.empty((h, w, 3), np.uint8)
    for y0 in range(0, h, _TONE_ROWS):
        y1 = min(h, y0 + _TONE_ROWS)
        rgb[y0:y1] = to_uint8(_tone(linear[y0:y1], image.domain, analysis, settings))
        # (dopo − prima) × 4 attorno al grigio medio
        diff[y0:y1] = cv2.addWeighted(rgb[y0:y1], 4.0, before[y0:y1], -4.0, 127.5)
    stats = ""
    try:
        # Stessi tasselli prima e dopo; il «dopo» in virgola mobile (l'arrotondamento a 8 bit
        # aggiungerebbe un rumore paragonabile a quello rimasto).
        after_tiles = [_luma(_tone(t, image.domain, analysis, settings)) for t in sample_tiles(linear, 1_000_000)]
        before_tiles = [_luma(t.astype(np.float32) / 255.0) for t in sample_tiles(before, 1_000_000)]
        s_before = estimate_sigma(before_tiles)
        s_after = estimate_sigma(after_tiles)
        if s_before > 1e-4:
            reduction = min(max(0.0, 1 - s_after / s_before) * 100, 99.0)
            stats = f"rumore stimato −{reduction:.0f}%"
    except Exception:
        pass
    return {"linear": linear, "rgb": rgb, "diff": diff, "stats": stats}


def _linkify(text: str) -> str:
    """Testo semplice -> HTML con a capo e indirizzi web cliccabili."""
    import html
    import re

    escaped = html.escape(text).replace("\n", "<br>")
    return re.sub(r"(https?://[^\s<)]+)", r"<a href='\1'>\1</a>", escaped)


def humanize_error(message: str) -> tuple[str, str]:
    """Trasforma un errore tecnico in (titolo, spiegazione con cosa fare)."""
    low = message.lower()
    if "memory" in low or "memoria" in low or "allocat" in low:
        return ("Memoria insufficiente",
                "Il computer ha esaurito la memoria. Prova «Blocchi di calcolo» più piccoli (256) nelle regolazioni "
                "avanzate del metodo, oppure chiudi altri programmi.")
    if "errno 13" in low or "permission denied" in low:
        return ("Permesso negato",
                "Non è possibile leggere o scrivere in quella cartella. Scegli un'altra cartella di salvataggio.")
    if "errno 28" in low or "no space" in low:
        return ("Disco pieno", "Lo spazio sul disco è esaurito: libera spazio o scegli un altro disco.")
    if "urlopen" in low or "http error" in low or "timed out" in low or "connection" in low:
        return ("Download non riuscito",
                "La connessione si è interrotta o il server non risponde. Non è stato salvato nulla: riprova più tardi.")
    return ("Operazione non riuscita", message)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Denoiser")
        self.setWindowIcon(app_icon())
        self.resize(1440, 900)
        self.setAcceptDrops(True)
        self._qs = QSettings("Denoiser", "Denoiser")
        self._tasks: set[Task] = set()
        self._image: ImageData | None = None
        self._analysis: Analysis | None = None
        self._path: Path | None = None
        self._base_rgb: np.ndarray | None = None
        self._generation = 0  # cambia a ogni nuova foto: i risultati vecchi vengono scartati
        self._loading = False
        self._base_request = 0
        self._preview_task: Task | None = None
        self._preview_pending = False
        self._preview_cache: tuple[str, tuple, np.ndarray] | None = None
        self._preview_running: tuple[str, tuple] | None = None  # (chiave, zona) dell'anteprima in corso
        self._preview_fraction = 0.0
        self._hint_action: str | None = None
        self._preview_started = 0.0
        self._job: Task | None = None
        self._job_kind = ""
        self._eta = EtaEstimator()
        self._downloading: set[str] = set()
        self._history: list[dict] = []
        self._history_index = -1
        self._applying_history = False
        self._preset_name = ""
        self._preset_snapshot: dict | None = None
        self._last_export: Path | None = None
        self._loading_path: Path | None = None
        self._loaded_develop: dict | None = None  # sviluppo con cui è stata aperta la foto mostrata
        self._loading_develop: dict | None = None
        self._pending_export: tuple | None = None
        self._presets: dict = {}
        self._retone_request = 0
        self._banner_serial = 0
        #: Riduzione del rumore applicata alla foto aperta (ogni nuova foto si apre senza).
        self._applied = False
        self._export_run: dict | None = None  # esportazione di più foto in corso
        self._proxy: np.ndarray | None = None  # copia ridotta della foto, per l'istogramma
        #: Foto a cui appartengono le regolazioni del pannello, e come erano all'ultimo salvataggio.
        self._settings_path: Path | None = None
        self._photo_snapshot: dict | None = None
        #: Cronologia di annulla/ripeti di ogni foto aperta in questa sessione.
        self._histories: dict[str, tuple[list[dict], int]] = {}

        self._build_central()
        self.panel = SettingsPanel(self._load_last_settings())
        self._build_dock()
        self._build_status_bar()
        self._build_actions()
        self._build_menus()
        self._build_toolbar()
        self._connect()
        self._refresh_presets()
        self._refresh_recent()
        self._update_dng_converter()
        self._push_history(force=True)
        self._update_enabled()
        self.keybar.setVisible(self.act_keybar.isChecked())
        geometry = self._qs.value("geometria")
        if geometry:
            self.restoreGeometry(geometry)
        state = self._qs.value("stato_finestra")
        if state:
            self.restoreState(state)

    # ================================================================== costruzione

    def _build_central(self) -> None:
        central = QWidget()
        v = QVBoxLayout(central)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        self.banner = QFrame()
        self.banner.setObjectName("banner")
        b = QHBoxLayout(self.banner)
        b.setContentsMargins(12, 6, 8, 6)
        self.banner_text = QLabel()
        self.banner_text.setTextFormat(Qt.TextFormat.RichText)
        self.banner_text.setWordWrap(True)
        # Un percorso lungo non deve allargare la finestra.
        self.banner_text.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
        b.addWidget(self.banner_text, 1)
        self.banner_buttons: list[QPushButton] = []
        for text, slot in (("Apri cartella", self._open_export_folder), ("Mostra il file", self._reveal_export),
                           ("Apri la foto", self._open_export_file)):
            button = QPushButton(text)
            button.clicked.connect(slot)
            b.addWidget(button)
            self.banner_buttons.append(button)
        close = QToolButton()
        close.setText("✕")
        close.setToolTip("Chiudi l'avviso")
        close.clicked.connect(self.banner.hide)
        b.addWidget(close)
        self.banner.hide()
        v.addWidget(self.banner)

        self.stack = QStackedWidget()
        self.welcome = self._build_welcome()
        self.view = SplitView()
        view_page = QWidget()
        pv = QVBoxLayout(view_page)
        pv.setContentsMargins(0, 0, 0, 0)
        pv.setSpacing(0)
        pv.addWidget(self.view, 1)
        self.filmstrip = Filmstrip(self._run)
        self.filmstrip.hide()
        pv.addWidget(self.filmstrip)
        self.keybar = KeyBar([
            ("Ctrl+Invio", "applica"),
            ("Rotellina", "zoom"),
            ("Trascina", "sposta la foto"),
            ("Doppio clic", "100% su quel punto"),
            ("Spazio", "tieni premuto per l'originale"),
            ("Pag↑ Pag↓", "foto precedente / successiva"),
            ("\\", "cambia confronto"),
            ("Ctrl+E", "esporta"),
            ("F1", "tutte le scorciatoie"),
        ])
        pv.addWidget(self.keybar)
        self._view_page = view_page
        self.stack.addWidget(self.welcome)
        self.stack.addWidget(view_page)
        v.addWidget(self.stack, 1)
        self.setCentralWidget(central)

    def _build_welcome(self) -> QWidget:
        page = QFrame()
        page.setObjectName("welcome")
        outer = QVBoxLayout(page)
        outer.addStretch(1)
        box = QFrame()
        box.setMaximumWidth(520)
        box.setStyleSheet("QFrame{border:1.5px dashed #555; border-radius:12px;}"
                          "QLabel,QPushButton{border:none;}")
        self._drop_box = box
        v = QVBoxLayout(box)
        v.setContentsMargins(36, 28, 36, 28)
        v.setSpacing(10)
        logo = QLabel()
        logo.setPixmap(app_icon().pixmap(64, 64))
        logo.setAlignment(Qt.AlignmentFlag.AlignCenter)
        title = QLabel("<span style='font-size:16pt; font-weight:600'>Togli il rumore dalle tue foto</span>")
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.drop_text = QLabel(_DROP_TEXT)
        self.drop_text.setAlignment(Qt.AlignmentFlag.AlignCenter)
        open_button = QPushButton(icon("open"), "  Apri una foto…   (Ctrl+O)")
        open_button.setObjectName("primary")
        open_button.setMinimumHeight(36)
        open_button.clicked.connect(self._open_dialog)
        folder_button = QPushButton(icon("batch"), "  Apri una cartella…   (Ctrl+Maiusc+O)")
        folder_button.setMinimumHeight(32)
        folder_button.setToolTip("Mostra tutte le foto della cartella in un carosello in basso, per scorrerle "
                                 "e sceglierne più d'una da esportare")
        folder_button.clicked.connect(self._open_folder_dialog)
        formats = QLabel("RAW (NEF, CR3, ARW, RAF, DNG…), JPG, PNG, TIFF")
        formats.setObjectName("hint")
        formats.setAlignment(Qt.AlignmentFlag.AlignCenter)
        steps = QLabel("<b>1</b> Apri &nbsp;·&nbsp; <b>2</b> Scegli il metodo e applica &nbsp;·&nbsp; <b>3</b> Esporta")
        steps.setAlignment(Qt.AlignmentFlag.AlignCenter)
        for w in (logo, title, self.drop_text, open_button, folder_button, formats, steps):
            v.addWidget(w)
        keys = KeyBar([("Ctrl+O", "apri"), ("Ctrl+Maiusc+O", "cartella"), ("F1", "guida e scorciatoie")])
        keys.setStyleSheet("QFrame#keybar{background:transparent;border:none;}")
        keys.layout().insertStretch(0, 1)
        keys_row = QHBoxLayout()
        keys_row.addWidget(keys)
        v.addLayout(keys_row)
        self.recent_box = QVBoxLayout()
        self.recent_title = QLabel("<b>Aperti di recente</b>")
        v.addSpacing(8)
        v.addWidget(self.recent_title)
        v.addLayout(self.recent_box)
        row = QHBoxLayout()
        row.addStretch(1)
        row.addWidget(box, 10)
        row.addStretch(1)
        outer.addLayout(row)
        outer.addStretch(1)
        return page

    def _build_dock(self) -> None:
        scroll = QScrollArea()
        scroll.setWidget(self.panel)
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        # Di norma non serve: la colonna si allarga quanto il contenuto (vedi _fit_dock). È solo una
        # rete di sicurezza perché nulla resti mai nascosto.
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        container = QWidget()
        v = QVBoxLayout(container)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        v.addWidget(scroll, 1)
        v.addWidget(self.panel.explain)
        self._dock_scroll, self._dock_container = scroll, container
        # La larghezza necessaria si conosce solo dopo stile, carattere e scala dello schermo (su Windows
        # Segoe UI e la scala al 125-150% cambiano tutto): la si ricalcola ogni volta che il pannello cambia.
        self.panel.installEventFilter(self)
        self._fit_dock()
        dock = QDockWidget("Impostazioni")
        dock.setObjectName("impostazioni")
        dock.setWidget(container)
        dock.setTitleBarWidget(QWidget())
        dock.setFeatures(QDockWidget.DockWidgetFeature.NoDockWidgetFeatures)
        self.addDockWidget(Qt.DockWidgetArea.RightDockWidgetArea, dock)

    def _build_status_bar(self) -> None:
        bar = self.statusBar()
        bar.setSizeGripEnabled(False)
        self.file_label = QLabel()
        bar.addWidget(self.file_label, 1)
        self.device_label = QLabel()
        self.device_label.setTextFormat(Qt.TextFormat.RichText)
        bar.addPermanentWidget(self.device_label)
        self.preview_time = QLabel()
        bar.addPermanentWidget(self.preview_time)
        self.job_label = QLabel()
        self.job_bar = QProgressBar()
        self.job_bar.setRange(0, 100)
        self.job_bar.setFixedWidth(180)
        self.job_cancel = QPushButton("Interrompi")
        self.job_cancel.setToolTip("Interrompe l'operazione in corso (Esc)")
        self.job_cancel.clicked.connect(self._cancel_job)
        for w in (self.job_label, self.job_bar, self.job_cancel):
            bar.addPermanentWidget(w)
            w.hide()
        self.zoom_button = QToolButton()
        self.zoom_button.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        self.zoom_button.setToolTip("Livello di ingrandimento")
        bar.addPermanentWidget(self.zoom_button)
        from ..neural.runtime import gpu_status

        has_gpu, text = gpu_status()
        color = OK if has_gpu else WARN
        short = "GPU" if has_gpu else "CPU"
        self.device_label.setText(f"<span style='color:{color}'>● {short}</span>")
        self.device_label.setToolTip(text + (". Le reti neurali useranno la scheda video." if has_gpu else
                                             ". Le reti neurali useranno il processore: funzionano, ma più lentamente."))

    def _action(self, text: str, slot, shortcut=None, tip: str = "", icon_name: str | None = None) -> QAction:
        act = QAction(text, self)
        if shortcut:
            act.setShortcut(QKeySequence(shortcut))
        if icon_name:
            act.setIcon(icon(icon_name))
        keys = shortcut_text(act)
        full_tip = (tip or text.replace("…", "")) + (f" ({keys})" if keys else "")
        act.setToolTip(full_tip)
        act.setStatusTip(tip or "")
        act.triggered.connect(slot)
        return act

    def _build_actions(self) -> None:
        a = self._action
        self.act_open = a("Apri…", self._open_dialog, "Ctrl+O", "Apri una foto (RAW, JPG, PNG, TIFF)", "open")
        self.act_open_folder = a("Apri cartella…", self._open_folder_dialog, "Ctrl+Shift+O",
                                 "Mostra tutte le foto di una cartella nel carosello in basso", "batch")
        self.act_apply = a("Applica", self._apply, "Ctrl+Return",
                           "Riduce il rumore della foto aperta con il metodo e le regolazioni scelti")
        self.act_export = a("Esporta…", self._export, "Ctrl+E",
                            "Scegli dove e come salvare la foto ripulita (formato, cartella, nome)", "export")
        self.act_batch = a("Esporta selezionate…", self._export_selected, "Ctrl+B",
                           "Esporta tutte le foto selezionate nel carosello, scegliendo metodo, cartella e formato",
                           "batch")
        self.act_prev = a("Foto precedente", lambda: self.filmstrip.step(-1), "PgUp", "Apre la foto precedente del carosello")
        self.act_next = a("Foto successiva", lambda: self.filmstrip.step(1), "PgDown", "Apre la foto successiva del carosello")
        self.act_out_folder = a("Apri la cartella di salvataggio", self._open_output_folder, None,
                                "Apre in Esplora risorse la cartella dove vengono salvate le foto")
        self.act_quit = a("Esci", self.close, "Ctrl+Q")
        self.act_undo = a("Annulla", self._undo, QKeySequence.StandardKey.Undo, "Annulla l'ultima regolazione", "undo")
        self.act_redo = a("Ripeti", self._redo, QKeySequence.StandardKey.Redo, "Ripeti la regolazione annullata", "redo")
        self.act_reset = a("Valori consigliati", self._reset_settings, None,
                           "Riporta riduzione del rumore e sviluppo ai valori consigliati", "reset")
        self.act_fit = a("Adatta alla finestra", lambda: self.view.fit(), "Ctrl+0", "Mostra tutta la foto", "fit")
        self.act_100 = a("100% (pixel reali)", lambda: self._zoom(1.0), "Ctrl+1",
                         "Pixel reali: il modo giusto per giudicare il rumore", "zoom100")
        self.act_200 = a("200%", lambda: self._zoom(2.0), "Ctrl+2")
        self.act_zoom_in = a("Ingrandisci", lambda: self.view.zoom_step(1), QKeySequence.StandardKey.ZoomIn)
        self.act_zoom_out = a("Riduci", lambda: self.view.zoom_step(-1), QKeySequence.StandardKey.ZoomOut)
        self.act_cycle = a("Cambia confronto", lambda: self.view.cycle_mode(), "\\",
                           "Alterna tendina, solo dopo, solo prima e rumore tolto", "compare")
        self.mode_group = QActionGroup(self)
        self.mode_actions: dict[str, QAction] = {}
        for key, label in MODES.items():
            act = QAction(label, self, checkable=True)
            act.triggered.connect(lambda _c=False, k=key: self.view.set_mode(k))
            self.mode_group.addAction(act)
            self.mode_actions[key] = act
        self.mode_actions["split"].setChecked(True)
        self.act_help = a("Guida e scorciatoie", self._help, "F1", "Come si usa e tutte le scorciatoie da tastiera", "help")
        self.act_about = a("Informazioni su Denoiser", self._about)
        self.act_cancel = a("Interrompi", self._cancel_job, "Esc")
        self.addAction(self.act_cancel)

    def _build_menus(self) -> None:
        mb = self.menuBar()
        m = mb.addMenu("&File")
        m.addAction(self.act_open)
        m.addAction(self.act_open_folder)
        self.recent_menu = m.addMenu("Apri recenti")
        m.addSeparator()
        for act in (self.act_export, self.act_batch):
            m.addAction(act)
        m.addSeparator()
        m.addAction(self.act_out_folder)
        m.addSeparator()
        m.addAction(self.act_quit)
        m = mb.addMenu("&Modifica")
        m.addAction(self.act_apply)
        m.addSeparator()
        for act in (self.act_undo, self.act_redo):
            m.addAction(act)
        m.addSeparator()
        m.addAction(self.act_reset)
        m = mb.addMenu("&Visualizza")
        for act in (self.act_fit, self.act_100, self.act_200, self.act_zoom_in, self.act_zoom_out):
            m.addAction(act)
        m.addSeparator()
        m.addAction(self.act_prev)
        m.addAction(self.act_next)
        m.addSeparator()
        m.addAction(self.act_cycle)
        for act in self.mode_actions.values():
            m.addAction(act)
        peek = QAction("Tieni premuto Spazio per vedere l'originale", self)
        peek.setEnabled(False)
        m.addAction(peek)
        m.addSeparator()
        self.act_keybar = QAction("Mostra i tasti sotto la foto", self, checkable=True)
        self.act_keybar.setChecked(self._qs.value("barra_tasti", True, type=bool))
        self.act_keybar.toggled.connect(self._toggle_keybar)
        m.addAction(self.act_keybar)
        self.preset_menu = mb.addMenu("&Preset")
        m = mb.addMenu("&Aiuto")
        m.addAction(self.act_help)
        m.addAction(self.act_about)
        zoom_menu = QMenu(self.zoom_button)
        zoom_menu.addAction(self.act_fit)
        for value in (0.25, 0.5, 1.0, 2.0, 4.0):
            act = QAction(f"{value * 100:.0f}%", zoom_menu)
            act.triggered.connect(lambda _c=False, z=value: self._zoom(z))
            zoom_menu.addAction(act)
        self.zoom_button.setMenu(zoom_menu)

    def _build_toolbar(self) -> None:
        bar = QToolBar("Principale")
        bar.setObjectName("principale")
        bar.setMovable(False)
        self.addToolBar(bar)
        # Ogni pulsante mostra la propria scorciatoia: si imparano usando l'app.
        open_button = ActionButton(self.act_open, "Apri file…")
        open_button.setObjectName("primary")
        self._open_button = open_button
        bar.addWidget(open_button)
        bar.addWidget(ActionButton(self.act_open_folder, "Apri cartella…"))
        bar.addSeparator()
        # Solo icona e tasto: il nome è nel suggerimento, così «Esporta» resta visibile anche a 1366 px.
        bar.addWidget(ActionButton(self.act_undo, show_text=False))
        bar.addWidget(ActionButton(self.act_redo, show_text=False))
        bar.addSeparator()
        bar.addWidget(ActionButton(self.act_fit, "Adatta"))
        bar.addWidget(ActionButton(self.act_100, "100%"))
        bar.addWidget(ActionButton(self.act_cycle, "Confronto"))
        modes = QToolButton()
        modes.setText("▾")
        modes.setToolTip("Scegli il tipo di confronto")
        modes.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        modes.setStyleSheet("QToolButton::menu-indicator { image: none; }")
        mode_menu = QMenu(modes)
        for act in self.mode_actions.values():
            mode_menu.addAction(act)
        modes.setMenu(mode_menu)
        bar.addWidget(modes)
        spacer = QWidget()
        spacer.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
        bar.addWidget(spacer)
        bar.addWidget(ActionButton(self.act_help, show_text=False))
        bar.addSeparator()
        export_button = ActionButton(self.act_export, "Esporta…")
        export_button.setObjectName("primary")
        bar.addWidget(export_button)
        export_selected = ActionButton(self.act_batch, "Esporta selezionate…")
        export_selected.setObjectName("primary")
        self.filmstrip.add_header_widget(export_selected)

    def _connect(self) -> None:
        self._preview_timer = QTimer(self, singleShot=True, interval=250)
        self._preview_timer.timeout.connect(self._start_preview)
        self._tone_timer = QTimer(self, singleShot=True, interval=150)
        self._tone_timer.timeout.connect(self._refresh_base)
        self._develop_timer = QTimer(self, singleShot=True, interval=400)
        self._develop_timer.timeout.connect(self._reload)
        self._save_timer = QTimer(self, singleShot=True, interval=500)
        self._save_timer.timeout.connect(self._save_last_settings)
        self._history_timer = QTimer(self, singleShot=True, interval=600)
        self._history_timer.timeout.connect(self._push_history)
        self._photo_timer = QTimer(self, singleShot=True, interval=500)
        self._photo_timer.timeout.connect(self._save_photo_settings)

        self.view.viewportChanged.connect(lambda *_: self._schedule_preview())
        self.view.zoomChanged.connect(lambda z: self.zoom_button.setText(f"Zoom {z * 100:.0f}%"))
        self.view.hintClicked.connect(self._on_hint_clicked)
        self.view.modeChanged.connect(lambda m: self.mode_actions[m].setChecked(True))
        self.panel.denoiseChanged.connect(self._on_denoise_changed)
        self.panel.toneChanged.connect(self._on_tone_changed)
        self.panel.developChanged.connect(self._on_develop_changed)
        self.panel.presetChosen.connect(self._apply_preset)
        self.panel.savePresetRequested.connect(self._save_preset)
        self.panel.resetRequested.connect(self._reset_settings)
        self.panel.downloadRequested.connect(self._download_model)
        self.panel.dngConverterChosen.connect(self._set_dng_converter)
        self.panel.dngConverterReset.connect(lambda: self._set_dng_converter(""))
        self.panel.useDngConverterToggled.connect(self._toggle_dng_converter)
        self.panel.applyRequested.connect(self._apply)
        self.panel.wbPickToggled.connect(self._toggle_wb_pick)
        self.view.imagePicked.connect(self._on_wb_picked)
        self.filmstrip.photoActivated.connect(self.open_path)
        self.filmstrip.selectionChanged.connect(self._update_enabled)
        self.filmstrip.closeRequested.connect(self._close_filmstrip)
        self.zoom_button.setText("Zoom —")

    # ================================================================== apertura

    def dragEnterEvent(self, event) -> None:
        if event.mimeData().hasUrls():
            event.acceptProposedAction()
            self._drop_box.setStyleSheet(self._drop_box.styleSheet().replace("#555", "#4a8fd9"))
            self.drop_text.setText("Rilascia per aprire")

    def dragLeaveEvent(self, event) -> None:
        self._reset_drop_box()

    def _reset_drop_box(self) -> None:
        self._drop_box.setStyleSheet(self._drop_box.styleSheet().replace("#4a8fd9", "#555"))
        self.drop_text.setText(_DROP_TEXT)

    def dropEvent(self, event) -> None:
        self._reset_drop_box()
        paths = [Path(u.toLocalFile()) for u in event.mimeData().urls() if u.isLocalFile()]
        folders = [p for p in paths if p.is_dir()]
        supported = [p for p in paths if p.is_file() and is_supported(p)]
        if not folders and len(supported) == 1:
            self.open_path(supported[0])
        elif len(folders) == 1 and not supported:
            self.open_folder(folders[0])
        elif folders or len(supported) > 1:
            self._show_photos(collect_inputs(folders + supported), None)
        else:
            self.statusBar().showMessage("Formato non supportato: si possono aprire RAW, JPG, PNG e TIFF.", 6000)

    def _open_dialog(self) -> None:
        start_dir = self._qs.value("ultima_cartella", "")
        path, _ = QFileDialog.getOpenFileName(self, "Apri foto", start_dir, file_dialog_filter())
        if path:
            self.open_path(Path(path))

    def _open_folder_dialog(self) -> None:
        start_dir = self._qs.value("ultima_cartella", "")
        folder = QFileDialog.getExistingDirectory(self, "Apri cartella", start_dir)
        if folder:
            self.open_folder(Path(folder))

    def open_folder(self, folder: Path) -> None:
        """Mostra le foto della cartella nel carosello e apre la prima."""
        photos = collect_inputs([folder])
        if not photos:
            self.statusBar().showMessage(f"Nella cartella {folder.name} non ci sono foto RAW, JPG, PNG o TIFF.", 6000)
            return
        self._qs.setValue("ultima_cartella", str(folder))
        self._show_photos(photos, folder)

    def _show_photos(self, photos: list[Path], folder: Path | None) -> None:
        if not photos:
            return
        self.filmstrip.set_photos(photos, folder)
        self.filmstrip.show()
        self._update_enabled()
        self.open_path(photos[0])

    def _close_filmstrip(self) -> None:
        self.filmstrip.clear()
        self.filmstrip.hide()
        self._update_enabled()

    def open_path(self, path: Path) -> None:
        if not self.filmstrip.isHidden() and not self.filmstrip.contains(path):
            self._close_filmstrip()  # foto di un'altra cartella: il carosello non la riguarda
        if path != self._settings_path:
            self._switch_photo_settings(path)
        self.filmstrip.set_shown(path)
        self._load(path, reset_view=True)

    # ================================================================== regolazioni di ogni foto

    def _settings_for_photo(self, path: Path) -> Settings:
        """Regolazioni salvate della foto; per una foto mai regolata: sviluppo neutro e la riduzione del
        rumore usata per ultima. Il salvataggio (cartella, formato) resta quello del pannello."""
        if path == self._settings_path:
            return self.panel.settings()
        current = self.panel.settings()
        base = Settings()
        base.denoise, base.output = current.denoise, current.output
        return apply_photo_settings(base, read_photo_settings(path))

    def _switch_photo_settings(self, path: Path) -> None:
        """Porta nel pannello le regolazioni di ``path`` (salvando prima quelle della foto precedente)."""
        self._flush_photo_settings()
        if self._settings_path is not None:
            self._histories[str(self._settings_path)] = (self._history, self._history_index)
        self._history_timer.stop()
        settings = self._settings_for_photo(path)
        self._set_panel(settings)
        self._settings_path = path
        self._photo_snapshot = settings.to_dict()
        stored = self._histories.get(str(path))
        if stored is not None:
            self._history, self._history_index = stored
        else:
            self._history, self._history_index = [], -1
            self._push_history(force=True)
        self._preset_name, self._preset_snapshot = "", None
        self._update_preset_label()
        self._update_enabled()

    def _flush_photo_settings(self) -> None:
        if self._photo_timer.isActive():
            self._photo_timer.stop()
            self._save_photo_settings()

    def _save_photo_settings(self) -> None:
        """Salva le regolazioni della foto aperta e, se richiesto, copia le modifiche sulle selezionate."""
        path, before = self._settings_path, self._photo_snapshot
        if path is None or before is None:
            return
        current = self.panel.settings()
        after = current.to_dict()
        if all(after[k] == before[k] for k in PHOTO_SECTIONS):
            return
        fields = changed_fields(before, after)
        self._photo_snapshot = after
        try:
            write_photo_settings(path, current)
        except OSError as exc:
            self.statusBar().showMessage(f"Regolazioni di {path.name} non salvate: {exc}", 8000)
            return
        written = [path]
        if self.filmstrip.sync_enabled() and fields:
            failed = []
            for other in self.filmstrip.selected_paths():
                if other == path:
                    continue
                try:
                    write_photo_settings(other, copy_fields(self._settings_for_photo(other), current, fields))
                    written.append(other)
                except OSError:
                    failed.append(other.name)
            message = f"Modifica applicata anche ad altre {len(written) - 1} foto selezionate."
            if failed:
                message += f" Non salvata per: {', '.join(failed[:3])}."
            self.statusBar().showMessage(message, 5000)
        self.filmstrip.set_edited(written)

    def _converter(self):
        if not self._qs.value("usa_dng_converter", True, type=bool):
            return None, False
        return find_converter(self._qs.value("dng_converter", "") or None), True

    def _load(self, path: Path, reset_view: bool) -> None:
        self._generation += 1
        generation = self._generation
        settings = self.panel.settings()
        self._loading = True
        self._loading_path = path
        self._loading_develop = raw_develop(settings.to_dict())
        self._update_enabled()
        if self.view.has_image():
            self.view.set_overlay(f"Apertura di {path.name}…" if reset_view else "Nuovo sviluppo del RAW in corso…")
        else:
            self.statusBar().showMessage(f"Apertura di {path.name}…")
        converter, use_converter = self._converter()

        def load(p: Path, develop):
            image = load_image(p, develop, use_converter, converter)
            return image, analyze(image), make_proxy(image.pixels)

        task = Task(load, path, settings.develop)
        task.signals.finished.connect(lambda res: self._on_loaded(generation, path, res, reset_view))
        task.signals.failed.connect(lambda msg, detail: self._on_load_failed(generation, path, msg, detail))
        self._run(task)

    def _reload(self) -> None:
        """Nuovo sviluppo del RAW dopo un cambio di ricostruzione dei colori."""
        # Se un'altra foto si sta aprendo, va riaperta quella (con il nuovo sviluppo), non la precedente.
        target = self._loading_path if self._loading else self._path
        if target is not None and kind_of(target) == "raw":
            self._load(target, reset_view=not self.view.has_image() or target != self._path)

    def _on_loaded(self, generation: int, path: Path, result, reset_view: bool) -> None:
        if generation != self._generation:
            return
        # Nuova generazione: le anteprime partite durante il caricamento (sulla foto precedente)
        # vengono scartate invece di essere mostrate e messe in cache come attuali.
        self._generation += 1
        self._loading = False
        self._loaded_develop = self._loading_develop
        self.statusBar().clearMessage()
        if path != self._path:
            # Ogni foto nuova si apre com'è: la riduzione del rumore parte solo con «Applica».
            self._applied = False
            self.view.set_original_only(True)
        self._image, self._analysis, self._proxy = result
        self._path = path
        self._preview_cache = None
        self._base_rgb = None  # la base vecchia non corrisponde più: l'anteprima aspetta quella nuova
        self.setWindowTitle(f"Denoiser — {path.name}")
        self._qs.setValue("ultima_cartella", str(path.parent))
        self._add_recent(path)
        self._update_info()
        self._update_histogram()
        self.stack.setCurrentWidget(self._view_page)
        self.view.set_overlay("")
        self._refresh_base(reset_view=reset_view)
        self._update_enabled()
        if self._pending_export is not None:
            pending, self._pending_export = self._pending_export, None
            QTimer.singleShot(0, lambda: self._export_to(*pending))

    def _on_load_failed(self, generation: int, path: Path, message: str, detail: str) -> None:
        if generation != self._generation:
            return
        self._loading = False
        self._pending_export = None
        self.view.set_overlay("")
        self.statusBar().clearMessage()
        self._update_enabled()
        self._show_error(f"Impossibile aprire {path.name}", message, detail)

    def _update_info(self) -> None:
        image, analysis = self._image, self._analysis
        if image is None or analysis is None:
            self.panel.set_image_context(None)
            self.file_label.setText("")
            return
        m = image.meta
        is_raw = image.domain.value == "linear"
        parts = [f"{image.width} × {image.height} px", f"{m.bits_per_sample} bit"]
        if m.camera:
            parts.append(m.camera)
        if m.iso:
            parts.append(f"ISO {m.iso:g}")
        level, detail = noise_judgement(image, analysis)
        if is_raw and uses_sensor_stage(image, self.panel.settings()):
            how = "Con «Grana» = 1 viene tolto esattamente questo, direttamente dai dati del sensore."
        elif is_raw:
            how = "Con «Grana» = 1 viene tolto il rumore misurato."
        else:
            how = "Nei JPG il rumore è già stato elaborato dalla fotocamera: può servire una forza un po' più alta di 1."
        noise = f"<b>Rumore: {level}</b> <span style='color:#9a9a9a'>({detail})</span><br><span style='color:#9a9a9a'>{how}</span>"
        notes = list(m.notes)
        if is_raw and image.raw_source is None:
            notes.append("Sensore non standard: il rumore viene ridotto dopo lo sviluppo.")
        self.panel.set_image_context({
            "title": m.source.name,
            "details": " · ".join(parts),
            "noise": noise,
            "notes": " · ".join(notes),
            "is_raw": is_raw,
            "is_bayer": image.raw_source is not None,
            "wb_equivalents": self._wb_equivalents(),
        })
        kind = m.source.suffix.upper().lstrip(".")
        self.file_label.setText(" · ".join([kind] + parts[2:]))

    def _wb_equivalents(self) -> dict:
        """Temperatura e tinta di «Come scattato», «Automatico» e «Luce diurna» per la foto aperta."""
        analysis = self._analysis
        color = analysis.color if analysis is not None else None
        if color is None:
            return {}
        result = {"camera": color.as_shot()}
        if analysis.auto_wb is not None:
            result["auto"] = temperature_from_multipliers(color.base * analysis.auto_wb, color.cam_from_xyz)
        if color.daylight is not None:
            result["daylight"] = temperature_from_multipliers(color.daylight, color.cam_from_xyz)
        return result

    def _update_histogram(self) -> None:
        """Istogramma della foto con lo sviluppo attuale (su una copia ridotta: pochi millisecondi)."""
        if self._proxy is None or self._analysis is None or self._image is None:
            self.panel.curve.set_histogram(None)
            return
        settings = self.panel.settings()
        rgb = render_tone(self._proxy, self._image.domain.value == "linear", self._analysis.tone_params(),
                          settings.develop, settings.tone)
        self.panel.curve.set_histogram(histogram(rgb))

    def _toggle_wb_pick(self, on: bool) -> None:
        self.view.set_pick_mode(on and self._image is not None)
        if on and self._image is not None:
            self.statusBar().showMessage("Fai clic su un punto che dovrebbe essere grigio o bianco (Esc per annullare).")
        else:
            self.statusBar().clearMessage()

    def _on_wb_picked(self, x: float, y: float) -> None:
        """Contagocce: il punto cliccato (media di un piccolo quadrato) diventa neutro."""
        image, analysis = self._image, self._analysis
        self.view.set_pick_mode(False)
        self.statusBar().clearMessage()
        if image is None or analysis is None:
            self.panel.pick_wb.setChecked(False)
            return
        r = max(2, int(min(image.width, image.height) / 400))
        xi, yi = int(x), int(y)
        patch = image.pixels[max(0, yi - r): yi + r + 1, max(0, xi - r): xi + r + 1].reshape(-1, 3)
        if image.domain.value != "linear":
            patch = srgb_decode(patch)
        mean = patch.mean(axis=0)
        if patch.size == 0 or mean.max() >= 0.98 or mean.min() <= 1e-4:
            self.panel.pick_wb.setChecked(False)
            self.statusBar().showMessage("Quel punto è bruciato o troppo scuro: scegline uno grigio o bianco ma "
                                         "con un po' di dettaglio.", 6000)
            return
        color = analysis.color or ColorInfo.for_display()
        temperature, tint = temperature_from_multipliers(color.base * color.gray_world(mean), color.cam_from_xyz)
        self.panel.set_wb(temperature, tint)

    # ================================================================== immagine «prima» e anteprima

    def _refresh_base(self, reset_view: bool = False) -> None:
        """Immagine «prima» a piena risoluzione (sviluppo e luminosità, senza denoise)."""
        if self._image is None or self._analysis is None:
            return
        self._base_request += 1
        request, generation = self._base_request, self._generation
        image, analysis, settings = self._image, self._analysis, self.panel.settings()
        task = Task(base_job, image, analysis, settings)
        task.signals.finished.connect(lambda levels: self._on_base(request, generation, levels, reset_view))
        task.signals.failed.connect(lambda msg, detail: generation == self._generation
                                    and self._show_error("Errore di visualizzazione", msg, detail))
        self._run(task)

    def _on_base(self, request: int, generation: int, levels, reset_view: bool) -> None:
        if request != self._base_request or generation != self._generation:
            return
        self._base_rgb = levels[0]  # uint8: 3 byte per pixel invece di 12
        self.view.set_base(levels, keep_view=not reset_view)
        if reset_view:
            # Si parte al 100% al centro: il rumore (e la sua scomparsa) si vede subito.
            self.view.zoom_to(1.0)
            self.view.center_on(self._image.width / 2, self._image.height / 2)
            self.view.setFocus()
        if not self._retone_cached_preview():
            self._schedule_preview()

    def _schedule_preview(self) -> None:
        if self._image is not None:
            self._preview_timer.start()

    def _preview_key(self, settings: Settings) -> str:
        return f"{self._generation}|" + json.dumps(settings.to_dict()["denoise"], sort_keys=True)

    def _retone_cached_preview(self) -> bool:
        """Dopo un cambio di luminosità riusa il denoise già calcolato: si rifà solo la resa tonale."""
        cache = self._preview_cache
        visible = self.view.visible_image_rect()
        settings = self.panel.settings()
        if cache is None or self._base_rgb is None or visible is None:
            return False
        key, rect, linear = cache
        if key != self._preview_key(settings) or not contains(rect, visible):
            return False
        x, y, w, h = rect
        task = Task(finish_preview, self._image, self._analysis, settings, linear,
                    self._base_rgb[y : y + h, x : x + w])
        self._retone_request += 1
        request, generation = self._retone_request, self._generation
        task.signals.finished.connect(
            lambda res: request == self._retone_request and generation == self._generation
            and self._show_preview(res, rect, 0.0))
        self._run(task)
        return True

    def _preview_target(self, visible) -> tuple[tuple[int, int, int, int], bool]:
        """Zona da elaborare per la vista attuale e True se è la foto intera.

        Da vicino basta la zona visibile (pochi istanti); con la foto rimpicciolita si elabora tutta,
        e il risultato serve poi a qualsiasi ingrandimento senza ricalcoli.
        """
        if visible[2] * visible[3] <= PREVIEW_MAX_PIXELS:
            return visible, False
        return (0, 0, self._image.width, self._image.height), True

    def _start_preview(self) -> None:
        if self._image is None or self._analysis is None or not self.view.has_image() or self._base_rgb is None:
            return
        visible = self.view.visible_image_rect()
        if visible is None:
            return
        if not self._applied:
            self.view.set_original_only(True)
            self._set_hint(_APPLY_HINT, "apply")
            return
        settings = self.panel.settings()
        key = self._preview_key(settings)
        if self._preview_task is not None:
            running_key, running_rect = self._preview_running
            _target, whole = self._preview_target(visible)
            # Il calcolo in corso copre già la zona visibile: lo si lascia finire. Se però era la foto
            # intera ed era appena partito e ora si guarda da vicino, conviene la sola zona (pochi istanti).
            if running_key == key and contains(running_rect, visible) and (whole or self._preview_fraction >= 0.5):
                return
            # Un'anteprima vecchia è ancora in corso: la si interrompe e si riparte appena finisce.
            self._preview_pending = True
            self._preview_task.cancel()
            return
        algorithm = self.panel.current_algorithm()
        if not algorithm.is_available():
            self.view.clear_patch()
            spec = algorithm.spec
            if spec.key in self._downloading:
                self._set_hint(f"Download di {spec.label} in corso…")
            else:
                self._set_hint(f"{spec.label} non è ancora sul computer. Fai clic qui per scaricarlo "
                               f"({spec.size_mb} MB, poi funziona offline).", "download")
            return
        self._set_hint("")
        if self._preview_cache is not None and self._preview_cache[0] == key and contains(self._preview_cache[1], visible):
            self.view.mark_fresh()
            return  # niente di nuovo da calcolare: il risultato mostrato copre già la zona visibile
        rect, whole = self._preview_target(visible)
        x, y, w, h = rect
        before = self._base_rgb[y : y + h, x : x + w]
        generation = self._generation
        task = Task(preview_job, self._image, self._analysis, settings, rect, before, with_progress=True)
        eta = EtaEstimator()
        task.signals.progress.connect(lambda f, _m: self._on_preview_progress(task, f, whole, eta))
        task.signals.finished.connect(lambda res: self._on_preview(task, generation, res, rect, key))
        task.signals.failed.connect(lambda msg, detail: self._on_preview_failed(task, msg))
        task.signals.cancelled.connect(lambda: self._on_preview_cancelled(task))
        self._preview_task = task
        self._preview_running = (key, rect)
        self._preview_fraction = 0.0
        self._preview_started = time.perf_counter()
        self.view.mark_stale()
        self.view.set_busy(-1)
        if whole:
            self._set_hint("Calcolo del risultato sulla foto intera…", "progress")
        self._run(task)

    def _on_preview_progress(self, task: Task, fraction: float, whole: bool, eta: EtaEstimator) -> None:
        if task is not self._preview_task:
            return
        self._preview_fraction = fraction
        self.view.set_busy(fraction if fraction > 0 else -1)
        if whole:
            text = "Calcolo del risultato sulla foto intera…"
            if fraction >= 0.995:
                text = "Risultato sulla foto intera quasi pronto…"
            elif fraction > 0:
                text += f" {fraction * 100:.0f}%"
                remaining = eta.text(fraction)
                if remaining:
                    text += f" · {remaining}"
            self._set_hint(text, "progress")

    def _preview_done(self, task: Task) -> bool:
        """Chiude il ciclo di un'anteprima; True se va ripartita subito."""
        if self._preview_task is task:
            self._preview_task = None
            self._preview_running = None
        self.view.set_busy(None)
        if self._hint_action == "progress":
            self._set_hint("")
        if self._preview_pending:
            self._preview_pending = False
            self._start_preview()
            return True
        return False

    def _on_preview(self, task: Task, generation: int, result, rect, key: str) -> None:
        elapsed = time.perf_counter() - self._preview_started
        if generation == self._generation:
            self._preview_cache = (key, rect, result["linear"])
            self._show_preview(result, rect, elapsed)
        self._preview_done(task)

    def _show_preview(self, result, rect, elapsed: float) -> None:
        self.view.set_patch(result["rgb"], rect, result["diff"])
        self.view.set_stats(result["stats"])
        if elapsed > 0:
            self.preview_time.setText(f"Anteprima: {format_duration(elapsed) if elapsed >= 60 else f'{elapsed:.1f} s'}"
                                      .replace(".", ","))

    def _on_preview_failed(self, task: Task, message: str) -> None:
        if self._preview_done(task):
            return
        _title, text = humanize_error(message)
        self._set_hint(f"Anteprima non riuscita: {text}")

    def _on_preview_cancelled(self, task: Task) -> None:
        self._preview_done(task)

    def _set_hint(self, text: str, action: str | None = None) -> None:
        """Messaggio al centro della vista. ``action``: "download" (un clic scarica il modello), "apply"
        (un clic applica la riduzione del rumore), "progress" (avanzamento della foto intera, tolto alla
        fine del calcolo) o None."""
        self._hint_action = action if text else None
        self.view.set_hint(text, clickable=action in ("download", "apply"))

    def _on_hint_clicked(self) -> None:
        algorithm = self.panel.current_algorithm()
        if self._hint_action == "apply":
            self._apply()
        elif self._hint_action == "download" and not algorithm.is_available():
            self._download_model(algorithm.key)

    def _apply(self) -> None:
        """Applica la riduzione del rumore alla foto aperta: da qui l'anteprima segue le regolazioni."""
        if self._image is None or self._applied:
            return
        self._applied = True
        self.view.set_original_only(False)
        self._set_hint("")
        self._update_enabled()
        self._preview_timer.stop()
        self._start_preview()
        if not self.panel.current_algorithm().is_available():
            self._ensure_model()  # propone subito il download del modello

    def _toggle_keybar(self, on: bool) -> None:
        self.keybar.setVisible(on)
        self._qs.setValue("barra_tasti", on)

    def _zoom(self, value: float) -> None:
        if self.view.has_image():
            self.view.zoom_to(value)
            self.view.setFocus()

    # ================================================================== cambi di impostazioni

    def _settings_changed_common(self) -> None:
        self._save_timer.start()
        self._photo_timer.start()
        if not self._applying_history:
            self._history_timer.start()
        self._update_preset_label()

    def _on_denoise_changed(self) -> None:
        self._settings_changed_common()
        self._update_info()
        self.view.mark_stale()
        self._schedule_preview()

    def _on_tone_changed(self) -> None:
        self._settings_changed_common()
        self.view.mark_stale()
        self._update_histogram()
        self._tone_timer.start()

    def _on_develop_changed(self) -> None:
        self._settings_changed_common()
        self.view.mark_stale()
        self._develop_timer.start()

    # ================================================================== annulla / ripeti

    def _push_history(self, force: bool = False) -> None:
        snapshot = self.panel.settings().to_dict()
        if not force and 0 <= self._history_index < len(self._history) and self._history[self._history_index] == snapshot:
            return
        self._history = self._history[: self._history_index + 1]
        self._history.append(snapshot)
        self._history = self._history[-100:]
        self._history_index = len(self._history) - 1
        self._update_enabled()

    def _restore(self, snapshot: dict) -> None:
        previous = self.panel.settings().to_dict()
        self._set_panel(Settings.from_dict(snapshot))
        self._after_bulk_change(previous)

    def _after_bulk_change(self, previous: dict) -> None:
        """Dopo preset, annulla/ripeti o ripristino: aggiorna solo ciò che serve."""
        current = self.panel.settings().to_dict()
        self._save_timer.start()
        self._photo_timer.start()
        self._update_preset_label()
        self._update_info()
        if raw_develop(current) != raw_develop(previous) and self._image is not None:
            self._develop_timer.start()
        elif current["tone"] != previous["tone"] or current["develop"] != previous["develop"]:
            self.view.mark_stale()
            self._update_histogram()
            self._tone_timer.start()
        self.view.mark_stale()
        self._schedule_preview()

    def _set_panel(self, settings: Settings) -> None:
        """Carica impostazioni nel pannello senza registrarle come nuova modifica nella cronologia."""
        self._applying_history = True
        try:
            self.panel.set_settings(settings)
        finally:
            self._applying_history = False

    def _undo(self) -> None:
        self._history_timer.stop()
        self._push_history()
        if self._history_index > 0:
            self._history_index -= 1
            self._restore(self._history[self._history_index])
        self._update_enabled()

    def _redo(self) -> None:
        if self._history_index < len(self._history) - 1:
            self._history_index += 1
            self._restore(self._history[self._history_index])
        self._update_enabled()

    def _reset_settings(self) -> None:
        previous = self.panel.settings()
        fresh = Settings()
        fresh.output = previous.output  # il salvataggio resta quello scelto dall'utente
        self._history_timer.stop()
        self._push_history()
        self._set_panel(fresh)
        self._preset_name, self._preset_snapshot = "Bilanciato", None
        self._push_history()
        self._after_bulk_change(previous.to_dict())
        self.statusBar().showMessage("Ripristinati i valori consigliati (Ctrl+Z per annullare).", 5000)

    # ================================================================== preset

    def _refresh_presets(self) -> None:
        self._presets = list_presets()  # letti dal disco una volta, non a ogni movimento dei cursori
        entries = [(name, desc) for name, (desc, _e) in self._presets.items()]
        self.panel.set_presets(entries)
        self.preset_menu.clear()
        builtin = [e for e in entries if e[0] in BUILTIN_PRESETS]
        mine = [e for e in entries if e[0] not in BUILTIN_PRESETS]
        for group in (builtin, mine):
            for name, desc in group:
                act = QAction(name, self.preset_menu)
                act.setToolTip(desc)
                act.triggered.connect(lambda _c=False, n=name: self._apply_preset(n))
                self.preset_menu.addAction(act)
            if group:
                self.preset_menu.addSeparator()
        save = QAction("Salva le impostazioni come preset…", self.preset_menu)
        save.triggered.connect(self._save_preset)
        self.preset_menu.addAction(save)
        delete = QAction("Elimina un mio preset…", self.preset_menu)
        delete.setEnabled(bool(mine))
        delete.triggered.connect(self._delete_preset)
        self.preset_menu.addAction(delete)
        folder = QAction("Apri la cartella dei preset", self.preset_menu)
        folder.triggered.connect(lambda: self._open_folder(user_presets_dir(), create=True))
        self.preset_menu.addAction(folder)
        self._update_preset_label()

    def _apply_preset(self, name: str) -> None:
        if not name:
            return
        try:
            content = preset_content(list_presets()[name][1])
        except (KeyError, PresetError) as exc:
            self._show_error("Preset non disponibile", str(exc), "")
            self._refresh_presets()
            return
        previous = self.panel.settings()
        self._history_timer.stop()
        self._push_history()
        try:
            new = apply_preset(previous, content)
        except (PresetError, ValueError, TypeError, AttributeError) as exc:
            self._show_error("Preset non valido", str(exc), "")
            return
        self._set_panel(new)
        self._preset_name = name
        self._preset_snapshot = self.panel.settings().to_dict()
        self._push_history()
        self._after_bulk_change(previous.to_dict())

    def _update_preset_label(self) -> None:
        entries = self._presets
        name = self._preset_name if self._preset_name in entries else ""
        description = entries[name][0] if name else "Impostazioni scelte da te."
        modified = False
        if name and self._preset_snapshot is not None:
            current = self.panel.settings().to_dict()
            modified = current["denoise"] != self._preset_snapshot["denoise"]
        self.panel.show_preset(name, description, modified)

    def _save_preset(self) -> None:
        dialog = QDialog(self)
        dialog.setWindowTitle("Salva come preset")
        v = QVBoxLayout(dialog)
        v.addWidget(QLabel("Nome del preset:"))
        name_edit = QLineEdit(self._preset_name if self._preset_name not in BUILTIN_PRESETS else "")
        v.addWidget(name_edit)
        v.addWidget(QLabel("Cosa salvare:"))
        boxes = {}
        for key, label in SECTIONS.items():
            box = QCheckBox(label)
            box.setChecked(key != "output")
            boxes[key] = box
            v.addWidget(box)
        hint = QLabel("Le sezioni non salvate restano quelle del momento quando applichi il preset.")
        hint.setObjectName("hint")
        hint.setWordWrap(True)
        v.addWidget(hint)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Save | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(dialog.accept)
        buttons.rejected.connect(dialog.reject)
        v.addWidget(buttons)
        if dialog.exec() != QDialog.DialogCode.Accepted:
            return
        name = name_edit.text().strip()
        sections = tuple(k for k, b in boxes.items() if b.isChecked())
        try:
            path = preset_path(name)
        except PresetError as exc:
            self._show_error("Nome non valido", str(exc), "")
            return
        if path.stem in BUILTIN_PRESETS:
            self._show_error("Nome già usato", f"«{path.stem}» è un preset predefinito: scegli un altro nome.", "")
            return
        if path.exists() and QMessageBox.question(self, "Preset esistente",
                                                  f"Esiste già un preset «{path.stem}». Sostituirlo?") != QMessageBox.StandardButton.Yes:
            return
        if not sections:
            return
        try:
            save_preset(self.panel.settings(), path, sections)
        except OSError as exc:
            self._show_error("Preset non salvato", str(exc), "")
            return
        self._preset_name = path.stem
        self._preset_snapshot = self.panel.settings().to_dict()
        self._refresh_presets()
        self.statusBar().showMessage(f"Preset «{path.stem}» salvato.", 5000)

    def _delete_preset(self) -> None:
        mine = sorted(p.stem for p in user_presets_dir().glob("*.json")) if user_presets_dir().is_dir() else []
        if not mine:
            return
        name, ok = QInputDialog.getItem(self, "Elimina preset", "Preset da eliminare:", mine, 0, False)
        if ok and name and QMessageBox.question(self, "Elimina preset", f"Eliminare il preset «{name}»?") == QMessageBox.StandardButton.Yes:
            try:
                (user_presets_dir() / f"{name}.json").unlink(missing_ok=True)
            except OSError as exc:
                self._show_error("Preset non eliminato", str(exc), "")
                return
            if self._preset_name == name:
                self._preset_name = ""
            self._refresh_presets()

    # ================================================================== modelli neurali

    def _download_model(self, key: str) -> None:
        from ..neural.models import MODELS, download

        spec = MODELS[key]
        if key in self._downloading:
            return
        if self._job is not None:
            self.statusBar().showMessage("Attendi la fine dell'operazione in corso.", 4000)
            return
        self._downloading.add(key)
        task = Task(download, spec, with_progress=True)

        def done(_path) -> None:
            self._downloading.discard(key)
            self._end_job()
            self.panel.refresh_algorithms()
            self.statusBar().showMessage(f"{spec.label} è pronto: l'anteprima si aggiorna da sola. D'ora in poi "
                                         "funziona anche offline.", 8000)
            self._schedule_preview()

        def failed(msg: str, detail: str) -> None:
            self._downloading.discard(key)
            self._end_job()
            self.panel.refresh_algorithms()
            self._show_error("Download non riuscito", msg, detail)

        def cancelled() -> None:
            self._downloading.discard(key)
            self._end_job()
            self.panel.refresh_algorithms()

        def progress(fraction: float, message: str) -> None:
            self._on_job_progress(fraction, message)
            self.panel.refresh_algorithms(download_text=f"Download: {int(fraction * 100)}%")

        task.signals.progress.connect(progress)
        task.signals.finished.connect(done)
        task.signals.failed.connect(failed)
        task.signals.cancelled.connect(cancelled)
        self._begin_job(task, "download", f"Download di {spec.label}…")
        self.panel.refresh_algorithms(download_text="Download in corso…")
        self._schedule_preview()

    def _ensure_model(self, key: str | None = None) -> bool:
        """True se il modello del metodo ``key`` (di norma quello del pannello) è sul computer;
        altrimenti propone di scaricarlo."""
        algorithm = get_algorithm(key) if key else self.panel.current_algorithm()
        if algorithm.is_available():
            return True
        spec = algorithm.spec
        answer = QMessageBox.question(
            self, f"Scarica {spec.label}",
            f"Per usare {spec.label} serve scaricare il modello ({spec.size_mb} MB) dalla pubblicazione ufficiale. "
            f"Si fa una sola volta, poi funziona offline.\n\nLicenza: {spec.license}\n\nScaricarlo ora?",
        )
        if answer == QMessageBox.StandardButton.Yes:
            self._download_model(algorithm.key)
        return False

    # ================================================================== lavori lunghi

    def _begin_job(self, task: Task, kind: str, message: str) -> None:
        self._job, self._job_kind = task, kind
        self._eta.reset()
        self.job_label.setText(message)
        self.job_bar.setRange(0, 0)
        for w in (self.job_label, self.job_bar, self.job_cancel):
            w.show()
        self._update_enabled()
        self._run(task)

    def _on_job_progress(self, fraction: float, message: str) -> None:
        self.job_bar.setRange(0, 100)
        self.job_bar.setValue(int(fraction * 100))
        eta = self._eta.text(fraction)
        self.job_label.setText(message + (f" · {eta}" if eta else ""))

    def _end_job(self) -> None:
        self._job, self._job_kind = None, ""
        for w in (self.job_label, self.job_bar, self.job_cancel):
            w.hide()
        self._update_enabled()

    def _cancel_job(self) -> None:
        if self.view.pick_mode:
            self.panel.pick_wb.setChecked(False)
            return
        if self._export_run is not None:
            self._export_run["cancelled"] = True  # anche se la foto in corso finisce prima di accorgersene
        if self._job is not None:
            self._job.cancel()
            self.job_label.setText("Interruzione in corso…")

    def _update_enabled(self) -> None:
        busy = self._job is not None or self._loading
        has_image = self._image is not None
        self.act_export.setEnabled(has_image and not busy)
        self.act_apply.setEnabled(has_image and not self._applied and not self._loading)
        self.panel.set_applied(self._applied if has_image else None, shortcut_text(self.act_apply))
        selected = len(self.filmstrip.selected_paths())
        self.act_batch.setEnabled(self._job is None and selected > 0)
        self.act_batch.setText(f"Esporta selezionate ({selected})…" if selected else "Esporta selezionate…")
        browsing = not self.filmstrip.isHidden() and len(self.filmstrip.paths()) > 1
        self.act_prev.setEnabled(browsing)
        self.act_next.setEnabled(browsing)
        self.act_undo.setEnabled(self._history_index > 0)
        self.act_redo.setEnabled(0 <= self._history_index < len(self._history) - 1)
        for act in (self.act_fit, self.act_100, self.act_200, self.act_zoom_in, self.act_zoom_out, self.act_cycle):
            act.setEnabled(has_image)

    # ================================================================== esportazione

    def _export(self) -> None:
        """Finestra «Esporta» per la foto aperta: dove, in che formato e con che nome salvarla."""
        if self._image is None or self._job is not None:
            return
        settings = self.panel.settings()
        is_raw = self._image.domain.value == "linear"
        dialog = ExportDialog(self, settings.output, self._image.meta, int(is_raw), int(not is_raw),
                              start_dir=str(self._path.parent) if self._path else "")
        accepted = dialog.exec() == QDialog.DialogCode.Accepted
        choice = dialog.choice()
        dialog.deleteLater()
        if accepted:
            self._remember_export_choice(choice)
            self._export_to(None)

    def _remember_export_choice(self, choice: ExportChoice) -> None:
        self.panel.set_output(choice.output(self.panel.settings().output))
        self._qs.setValue("formato_non_raw", choice.non_raw_format)
        self._qs.setValue("se_esiste", choice.policy)

    def _non_raw_format(self) -> str:
        """Formato per JPG, PNG e TIFF quando l'ultima esportazione era in DNG."""
        value = self._qs.value("formato_non_raw", "tiff")
        return value if value in ("tiff", "png", "jpeg") else "tiff"

    def _export_to(self, destination: Path | None, fmt: str | None = None, confirmed: bool = False) -> None:
        if self._image is None or self._analysis is None:
            QMessageBox.information(self, "Esporta", "Apri prima una foto.")
            return
        if self._job is not None:
            return
        settings = self.panel.settings()
        develop_changed = (kind_of(self._path) == "raw" and
                           (self._develop_timer.isActive() or raw_develop(settings.to_dict()) != self._loaded_develop))
        if self._loading or develop_changed:
            # La foto sta per essere risviluppata (bilanciamento o colori appena cambiati): si esporta
            # appena è pronta, altrimenti il file avrebbe lo sviluppo precedente.
            self._pending_export = (destination, fmt, confirmed)
            if develop_changed and not self._loading:
                self._develop_timer.stop()
                self._reload()
            self.statusBar().showMessage("Esportazione appena terminato il nuovo sviluppo del RAW…", 8000)
            return
        if not self._ensure_model():
            return
        if fmt:
            settings.output.format = fmt
        if settings.output.format == "dng" and self._image.domain.value != "linear":
            # Il DNG si crea solo dai RAW: si usa il formato scelto per le foto non RAW.
            settings.output.format = self._non_raw_format()
        if destination is None:
            destination = output_path(self._image.meta, settings.output, settings.denoise.algorithm)
        if is_source(destination, self._image.meta.source):
            # Stesso nome, formato e cartella dell'originale: l'originale non si sovrascrive mai.
            destination = unique_path(destination)
            confirmed = True
        if destination.exists() and not settings.output.overwrite and not confirmed:
            box = QMessageBox(self)
            box.setWindowTitle("File esistente")
            box.setText(f"{destination.name} esiste già in {destination.parent}.")
            overwrite = box.addButton("Sovrascrivi", QMessageBox.ButtonRole.DestructiveRole)
            rename = box.addButton("Salva con un altro nome", QMessageBox.ButtonRole.AcceptRole)
            box.addButton("Annulla", QMessageBox.ButtonRole.RejectRole)
            box.exec()
            if box.clickedButton() is rename:
                destination = unique_path(destination)
            elif box.clickedButton() is not overwrite:
                return
        image, analysis = self._image, self._analysis
        notes: list[str] = []
        started = time.perf_counter()
        algorithm = self.panel.current_algorithm()
        from ..neural.runtime import gpu_status

        message = f"Esportazione di {destination.name}…"
        if algorithm.joint and not gpu_status()[0]:
            message = f"{algorithm.label} sul processore: servono alcuni minuti. Puoi continuare a esplorare l'anteprima."
        task = Task(export_image, image, analysis, settings, destination, with_progress=True, notes=notes)
        task.signals.progress.connect(self._on_job_progress)
        task.signals.finished.connect(lambda path: self._on_exported(path, notes, settings, time.perf_counter() - started))
        task.signals.failed.connect(lambda msg, detail: (self._end_job(), self._show_error("Esportazione non riuscita", msg, detail)))
        task.signals.cancelled.connect(lambda: (self._end_job(), self.statusBar().showMessage(
            "Esportazione interrotta: non è stato salvato nulla.", 6000)))
        self._begin_job(task, "export", message)

    def _on_exported(self, path: Path, notes: list[str], settings: Settings, elapsed: float) -> None:
        self._end_job()
        self._last_export = Path(path)
        try:
            nbytes = Path(path).stat().st_size
            size = f"{nbytes / 1e6:.0f} MB" if nbytes >= 1e6 else f"{max(1, round(nbytes / 1e3))} kB"
        except OSError:
            size = ""
        fmt = settings.output.format.upper()
        if settings.output.format in ("tiff", "png"):
            fmt += f" {settings.output.bit_depth} bit"
        d = settings.denoise
        algorithm = get_algorithm(d.algorithm)  # quello usato per il file, non quello scelto nel frattempo
        summary = " · ".join(x for x in (fmt, size, format_duration(elapsed),
                                         f"{algorithm.label}, grana {d.luma_strength:.2f} / colore {d.chroma_strength:.2f}".replace(".", ",")) if x)
        extra = f"<br><span style='color:#e0b050'>{'; '.join(notes)}</span>" if notes else ""
        self.banner_text.setText(f"<b>Fatto!</b> {Path(path).name} salvato in {Path(path).parent}"
                                 f"<br><span style='color:#a8c0dc'>{summary}</span>{extra}")
        for button in self.banner_buttons:
            button.show()
        self.banner.show()
        self._banner_serial += 1
        serial = self._banner_serial
        QTimer.singleShot(30000, lambda: serial == self._banner_serial and self.banner.hide())

    def _open_folder(self, folder: Path, create: bool = False) -> None:
        if create:
            try:
                folder.mkdir(parents=True, exist_ok=True)
            except OSError as exc:
                self._show_error("Cartella non disponibile", str(exc), "")
                return
        if folder.is_dir():
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(folder)))

    def _open_export_folder(self) -> None:
        if self._last_export:
            self._open_folder(self._last_export.parent)

    def _open_export_file(self) -> None:
        if self._last_export and self._last_export.exists():
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(self._last_export)))

    def _reveal_export(self) -> None:
        if not self._last_export:
            return
        if sys.platform == "win32":
            # Explorer vuole «/select,"percorso"» come un unico argomento.
            subprocess.Popen(f'explorer /select,"{self._last_export}"')
        else:
            self._open_export_folder()

    def _open_output_folder(self) -> None:
        if self._image is not None:
            settings = self.panel.settings()
            self._open_folder(output_path(self._image.meta, settings.output, "x").parent, create=True)
        elif self._last_export:
            self._open_export_folder()

    # ================================================================== esportazione di più foto

    def _export_selected(self) -> None:
        """Esporta le foto selezionate nel carosello, ognuna con le sue regolazioni."""
        if self._job is not None:
            return
        paths = self.filmstrip.selected_paths()
        if not paths:
            self.statusBar().showMessage("Seleziona nel carosello le foto da esportare (Ctrl+clic o Maiusc+clic).", 6000)
            return
        self._flush_photo_settings()
        settings = self.panel.settings()
        raw = sum(1 for p in paths if kind_of(p) == "raw")
        example = ImageMeta(source=paths[0], kind=kind_of(paths[0]) or "")
        policy = self._qs.value("se_esiste", "rename")
        dialog = ExportDialog(self, settings.output, example, raw, len(paths) - raw,
                              algorithm="", non_raw_format=self._non_raw_format(),
                              policy=policy if isinstance(policy, str) else "rename", start_dir=str(paths[0].parent))
        accepted = dialog.exec() == QDialog.DialogCode.Accepted
        choice = dialog.choice()
        dialog.deleteLater()
        if not accepted:
            return
        self._remember_export_choice(choice)
        output = self.panel.settings().output
        per_photo = {}
        for path in paths:  # ogni foto con le sue regolazioni; il metodo scelto nella finestra vale per tutte
            photo = self._settings_for_photo(path)
            photo.output = output
            if choice.algorithm and photo.denoise.algorithm != choice.algorithm:
                photo.denoise.algorithm, photo.denoise.params = choice.algorithm, {}
            per_photo[str(path)] = photo
        for key in sorted({photo.denoise.algorithm for photo in per_photo.values()}):
            if not self._ensure_model(key):  # propone il download; si riprova a scaricamento finito
                return
        self._export_run = {
            "paths": paths, "index": 0, "settings": per_photo, "choice": choice,
            "results": [], "outputs": [], "started": time.perf_counter(),
        }
        self._export_next()

    def _export_next(self) -> None:
        run = self._export_run
        if run is None:
            return
        if run.get("cancelled"):
            self._export_finished(interrupted=True)
            return
        if run["index"] >= len(run["paths"]):
            self._export_finished(interrupted=False)
            return
        path = run["paths"][run["index"]]
        converter, use_converter = self._converter()
        settings = settings_for(path, run["settings"][str(path)], run["choice"].non_raw_format)
        task = Task(export_one, path, settings, run["choice"].policy, converter, use_converter,
                    with_progress=True, taken=frozenset(run["outputs"]))
        task.signals.progress.connect(lambda f, m, p=path: self._on_export_progress(p, f, m))
        task.signals.finished.connect(lambda res, p=path: self._export_one_done(p, res))
        task.signals.failed.connect(lambda msg, _d, p=path: self._export_one_done(p, ("errore", None, [humanize_error(msg)[1]])))
        task.signals.cancelled.connect(lambda: self._export_finished(interrupted=True))
        message = f"Esportazione 1 di {len(run['paths'])}: {path.name}…"
        if run["index"] == 0:
            self._begin_job(task, "export-many", message)
        else:
            self._job = task
            self._run(task)

    def _on_export_progress(self, path: Path, fraction: float, message: str) -> None:
        run = self._export_run
        if run is None:
            return
        total = len(run["paths"])
        overall = (run["index"] + fraction) / total
        self.job_bar.setRange(0, 100)
        self.job_bar.setValue(int(overall * 100))
        eta = self._eta.text(overall)
        self.job_label.setText(f"Esportazione {run['index'] + 1} di {total}: {path.name} · {message}"
                               + (f" · {eta} in tutto" if eta else ""))

    def _export_one_done(self, path: Path, result) -> None:
        run = self._export_run
        if run is None:
            return
        outcome, saved, notes = result
        if outcome == "ok" and saved is not None:
            run["outputs"].append(saved)
        run["results"].append((path, outcome, "; ".join(notes)))
        run["index"] += 1
        self._export_next()

    def _export_finished(self, interrupted: bool) -> None:
        run, self._export_run = self._export_run, None
        self._end_job()
        if run is None:
            return
        results, outputs = run["results"], run["outputs"]
        ok = sum(1 for _, outcome, _ in results if outcome == "ok")
        skipped = sum(1 for _, outcome, _ in results if outcome == "saltata")
        errors = [(p, n) for p, outcome, n in results if outcome == "errore"]
        elapsed = format_duration(time.perf_counter() - run["started"])
        if outputs:
            self._last_export = Path(outputs[-1])
        folders = {Path(o).parent for o in outputs}
        where = f" in {next(iter(folders))}" if len(folders) == 1 else (" nelle cartelle scelte" if folders else "")
        total = len(run["paths"])
        if skipped == total:
            text = f"<b>Nessuna foto salvata:</b> {'la foto esiste' if total == 1 else f'le {total} foto esistono'} " \
                   "già nella cartella scelta."
        else:
            head = "Esportazione interrotta" if interrupted else ("Fatto!" if not errors else "Esportazione terminata")
            text = f"<b>{head}</b> {ok} foto su {total} salvate{where} in {elapsed}"
            if skipped:
                text += f" · {skipped} saltate perché già presenti"
        if errors:
            listed = "<br>".join(f"{p.name}: {n}" for p, n in errors[:5])
            more = f"<br>… e altre {len(errors) - 5}" if len(errors) > 5 else ""
            text += f"<br><span style='color:#e0b050'>{len(errors)} non esportate:<br>{listed}{more}</span>"
        notes = [n for _, outcome, n in results if outcome == "ok" and n]
        if notes:
            text += f"<br><span style='color:#e0b050'>{'; '.join(sorted(set(notes))[:3])}</span>"
        self.banner_text.setText(text)
        for button in self.banner_buttons:
            button.setVisible(bool(outputs))
        self.banner.show()
        self._banner_serial += 1
        serial = self._banner_serial
        if not errors:
            QTimer.singleShot(30000, lambda: serial == self._banner_serial and self.banner.hide())
        QApplication.alert(self)

    # ================================================================== impostazioni ricordate

    def _load_last_settings(self) -> Settings:
        raw = self._qs.value("ultime_impostazioni", "")
        try:
            settings = Settings.from_dict(json.loads(raw)) if raw else Settings()
        except (ValueError, TypeError, AttributeError, KeyError):
            settings = Settings()
        settings.output.overwrite = False
        if not self._qs.value("nome_originale", False, type=bool):
            # Dalla 0.12.0 la foto esportata tiene il nome dell'originale (prima si aggiungeva «_dn»).
            if settings.output.name_template == "{nome}_dn":
                settings.output.name_template = "{nome}"
            self._qs.setValue("nome_originale", True)
        return settings

    def _save_last_settings(self) -> None:
        settings = self.panel.settings()
        settings.output.overwrite = False
        self._qs.setValue("ultime_impostazioni", json.dumps(settings.to_dict()))

    def _update_dng_converter(self) -> None:
        enabled = self._qs.value("usa_dng_converter", True, type=bool)
        found = find_converter(self._qs.value("dng_converter", "") or None)
        self.panel.set_dng_converter(str(found) if found else None, enabled)

    def _set_dng_converter(self, path: str) -> None:
        self._qs.setValue("dng_converter", path)
        self._update_dng_converter()

    def _toggle_dng_converter(self, enabled: bool) -> None:
        self._qs.setValue("usa_dng_converter", enabled)

    def _recent(self) -> list[str]:
        value = self._qs.value("recenti", [])
        if isinstance(value, str):
            value = [value]
        return [v for v in (value or []) if isinstance(v, str)]

    def _add_recent(self, path: Path) -> None:
        items = [str(path)] + [p for p in self._recent() if p != str(path)]
        self._qs.setValue("recenti", items[:_MAX_RECENT])
        self._refresh_recent()

    def _refresh_recent(self) -> None:
        items = [p for p in self._recent() if Path(p).exists()]
        self.recent_menu.clear()
        for p in items:
            act = QAction(Path(p).name, self.recent_menu)
            act.setToolTip(p)
            act.triggered.connect(lambda _c=False, path=p: self.open_path(Path(path)))
            self.recent_menu.addAction(act)
        self.recent_menu.setEnabled(bool(items))
        while self.recent_box.count():
            w = self.recent_box.takeAt(0).widget()
            if w is not None:
                w.deleteLater()
        for p in items[:5]:
            # Percorsi lunghi accorciati al centro: un pulsante largo allargherebbe tutta la finestra.
            text = f"{Path(p).name}   —   {Path(p).parent}"
            button = QPushButton(QFontMetrics(self.font()).elidedText(text, Qt.TextElideMode.ElideMiddle, 420))
            button.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
            button.setObjectName("link")
            button.setToolTip(p)
            button.clicked.connect(lambda _c=False, path=p: self.open_path(Path(path)))
            self.recent_box.addWidget(button)
        self.recent_title.setVisible(bool(items))

    # ================================================================== aiuto ed errori

    def _show_error(self, title: str, message: str, detail: str) -> None:
        human_title, text = humanize_error(message)
        box = QMessageBox(self)
        box.setIcon(QMessageBox.Icon.Warning)
        box.setWindowTitle(title)
        box.setText(f"<b>{human_title if human_title != 'Operazione non riuscita' else title}</b>")
        box.setInformativeText(_linkify(text))
        for label in box.findChildren(QLabel):  # i link (es. download di Adobe DNG Converter) si aprono
            label.setOpenExternalLinks(True)
        if detail:
            box.setDetailedText(detail)
        box.exec()

    def _help(self) -> None:
        QMessageBox.information(
            self, "Guida rapida",
            "<h3>Come si usa</h3>"
            "<ol><li><b>Apri</b> una foto (Ctrl+O) o una <b>cartella</b> (Ctrl+Maiusc+O), anche trascinandola nella "
            "finestra. Le foto della cartella compaiono nel <b>carosello</b> in basso: un clic apre una foto, "
            "Pag↑ e Pag↓ passano alla precedente o alla successiva.</li>"
            "<li>La foto si apre al <b>100%</b> così com'è. Scegli il <b>metodo</b> nel pannello e premi "
            "<b>Applica</b> (Ctrl+Invio): a sinistra della linea l'originale, a destra il risultato. Da quel momento "
            "il risultato segue le regolazioni; ogni nuova foto riparte dall'originale.</li>"
            "<li>Nel pannello <b>Sviluppo</b> regola bilanciamento (anche con il contagocce), esposizione, toni "
            "e la curva sopra l'istogramma. Ogni foto ricorda le sue regolazioni; per una raffica attiva "
            "<i>Applica le modifiche a tutte le selezionate</i> nel carosello.</li>"
            "<li>Regola <b>Grana</b> e <b>Macchie di colore</b> (1 = rumore misurato). Tutto il resto ha già "
            "valori consigliati; la spiegazione di ogni controllo compare in basso nel pannello.</li>"
            "<li><b>Esporta</b> (Ctrl+E): nella finestra scegli dove salvare, il formato (TIFF, PNG, JPEG o DNG) "
            "e il nome; la volta dopo ritrovi le stesse scelte. Per più foto: selezionale nel carosello "
            "con Ctrl+clic o Maiusc+clic e usa <b>Esporta selezionate</b> (Ctrl+B).</li></ol>"
            "<h3>Scorciatoie</h3><table>"
            "<tr><td>Ctrl+O / Ctrl+Maiusc+O</td><td>Apri una foto / una cartella</td></tr>"
            "<tr><td>Ctrl+Invio</td><td>Applica la riduzione del rumore</td></tr>"
            "<tr><td>Pag↑ / Pag↓</td><td>Foto precedente / successiva del carosello</td></tr>"
            "<tr><td>Ctrl+E</td><td>Esporta</td></tr>"
            "<tr><td>Ctrl+B</td><td>Esporta le foto selezionate</td></tr><tr><td>Ctrl+Z / Ctrl+Y</td><td>Annulla / Ripeti</td></tr>"
            "<tr><td>Ctrl+0 / Ctrl+1 / Ctrl+2</td><td>Adatta / 100% / 200%</td></tr>"
            "<tr><td>Ctrl + / Ctrl −, rotellina</td><td>Zoom</td></tr><tr><td>Frecce (+ Maiusc)</td><td>Sposta la vista</td></tr>"
            "<tr><td>Spazio (tenuto)</td><td>Mostra l'originale</td></tr>"
            "<tr><td>\\</td><td>Tendina → solo dopo → solo prima → rumore tolto</td></tr>"
            "<tr><td>Doppio clic su un cursore</td><td>Valore consigliato</td></tr>"
            "<tr><td>Esc</td><td>Interrompe l'operazione in corso o il contagocce</td></tr></table>"
            "<p>Vista <b>«rumore tolto»</b>: mostra ciò che è stato rimosso. Se vedi solo grana uniforme il dettaglio "
            "è salvo; se compaiono contorni, abbassa «Grana» o alza «Recupero dettaglio».</p>",
        )

    def _about(self) -> None:
        QMessageBox.about(
            self, "Denoiser",
            f"<b>Denoiser {__version__}</b><br>Riduzione del rumore per foto RAW, JPG e PNG, interamente in locale."
            "<br><br>Modelli neurali, scaricati dalle pubblicazioni ufficiali: DRUNet (MIT) e SCUNet (Apache 2.0) "
            "di Kai Zhang; Restormer (MIT) di Syed Waqas Zamir e altri; RawNIND (CC BY 4.0) di Benoit Brummer "
            "e Christophe De Vleeschouwer, nella versione ONNX di darktable. Lettura dei RAW: LibRaw. "
            "Metadati: ExifTool di Phil Harvey."
            "<br><br>Codice con licenza MIT · "
            "<a href='https://github.com/foyer-labs/Image-Denoiser'>github.com/foyer-labs/Image-Denoiser</a>",
        )

    # ================================================================== utilità

    def _run(self, task: Task) -> None:
        self._tasks.add(task)
        for signal in (task.signals.finished, task.signals.failed, task.signals.cancelled):
            signal.connect(lambda *_: self._tasks.discard(task))
        start(task)

    def _fit_dock(self) -> None:
        """La colonna delle impostazioni è larga almeno quanto il suo contenuto (niente tagliato a destra)."""
        scroll = self._dock_scroll
        needed = (self.panel.minimumSizeHint().width() + scroll.verticalScrollBar().sizeHint().width()
                  + 2 * scroll.frameWidth() + 2)
        if needed != self._dock_container.minimumWidth():
            self._dock_container.setMinimumWidth(max(400, needed))

    def eventFilter(self, obj, event) -> bool:  # noqa: N802 (API Qt)
        if obj is self.panel and event.type() in (
            QEvent.Type.LayoutRequest, QEvent.Type.Show, QEvent.Type.Polish, QEvent.Type.FontChange,
            QEvent.Type.StyleChange, QEvent.Type.ScreenChangeInternal,
        ):
            QTimer.singleShot(0, self._fit_dock)
        return super().eventFilter(obj, event)

    def showEvent(self, event) -> None:
        super().showEvent(event)
        dark_title_bar(self)
        QTimer.singleShot(0, self._fit_dock)

    def closeEvent(self, event) -> None:
        if self._job is not None:
            answer = QMessageBox.question(
                self, "Operazione in corso",
                "C'è un'operazione in corso. Interromperla e uscire? (Nessun file verrà lasciato a metà.)",
            )
            if answer != QMessageBox.StandardButton.Yes:
                event.ignore()
                return
            self._job.cancel()
        for task in list(self._tasks):
            task.cancel()
        QThreadPool.globalInstance().waitForDone(20000)  # i lavori annullati si fermano al passo successivo
        from ..neural.runtime import release_sessions

        release_sessions()  # libera la scheda video prima di uscire
        self._flush_photo_settings()
        self._save_last_settings()
        self._qs.setValue("geometria", self.saveGeometry())
        self._qs.setValue("stato_finestra", self.saveState())
        super().closeEvent(event)


__all__ = ["MainWindow"]
