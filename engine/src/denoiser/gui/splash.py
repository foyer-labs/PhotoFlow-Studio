"""Schermata di avvio con barra di avanzamento.

All'avvio l'app carica librerie pesanti (calcolo numerico, immagini, RAW, reti neurali): senza un
segnale immediato l'utente non capisce se il doppio clic ha funzionato. La schermata compare appena
Python e Qt sono pronti e mostra l'avanzamento reale del caricamento.

Questo modulo usa solo Qt: va importato prima delle librerie pesanti.
"""

from __future__ import annotations

import importlib

from PySide6.QtCore import QRectF, Qt
from PySide6.QtGui import QColor, QFont, QPainter, QPainterPath, QPixmap
from PySide6.QtWidgets import QApplication, QSplashScreen

from .style import ACCENT, app_icon

WIDTH, HEIGHT = 520, 300
#: Posizione del testo di stato (in basso a sinistra).
STATUS_X, STATUS_BASELINE = 32, 247

#: Fasi di caricamento: (modulo da importare, testo per l'utente, peso sul tempo totale).
#: I pesi vengono dalle misure dei tempi di avvio.
STEPS = (
    ("numpy", "Calcolo numerico", 8),
    ("cv2", "Elaborazione delle immagini", 6),
    ("scipy.ndimage", "Librerie scientifiche", 16),
    ("denoiser.pipeline", "Algoritmi di riduzione del rumore", 28),
    ("onnx", "Reti neurali", 6),
    ("onnxruntime", "Scheda video", 10),
    ("denoiser.gui.main_window", "Interfaccia", 6),
)
#: Peso della costruzione della finestra principale (dopo le importazioni).
WINDOW_WEIGHT = 20


def render_splash(
    fraction: float | None, status: str = "", version: str = "", dpr: float = 1.0
) -> QPixmap:
    """Disegna la schermata. ``fraction`` None: senza barra piena né percentuale."""
    pm = QPixmap(round(WIDTH * dpr), round(HEIGHT * dpr))
    pm.setDevicePixelRatio(dpr)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setRenderHint(QPainter.RenderHint.TextAntialiasing)

    card = QRectF(0.5, 0.5, WIDTH - 1, HEIGHT - 1)
    path = QPainterPath()
    path.addRoundedRect(card, 14, 14)
    p.fillPath(path, QColor("#1f2329"))
    p.setPen(QColor("#3a4049"))
    p.drawPath(path)

    p.drawPixmap(round((WIDTH - 72) / 2), 34, app_icon().pixmap(72, 72))
    family = QApplication.font().family()
    title = QFont(family)
    title.setPixelSize(30)
    title.setWeight(QFont.Weight.DemiBold)
    p.setFont(title)
    p.setPen(QColor("#f0f0f0"))
    p.drawText(QRectF(0, 116, WIDTH, 40), Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter, "Denoiser")
    sub = QFont(family)
    sub.setPixelSize(14)
    p.setFont(sub)
    p.setPen(QColor("#a8adb5"))
    p.drawText(QRectF(0, 156, WIDTH, 24), Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
               "Riduzione del rumore per foto RAW, JPG e PNG")
    if version:
        small = QFont(family)
        small.setPixelSize(12)
        p.setFont(small)
        p.setPen(QColor("#7d838c"))
        p.drawText(QRectF(0, 180, WIDTH, 20), Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
                   f"versione {version}")

    # Stato e percentuale
    text = QFont(family)
    text.setPixelSize(13)
    p.setFont(text)
    p.setPen(QColor("#c8ccd2"))
    if status:
        p.drawText(STATUS_X, STATUS_BASELINE, status)
    if fraction is not None:
        p.drawText(QRectF(0, STATUS_BASELINE - 16, WIDTH - STATUS_X, 20),
                   Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter, f"{round(fraction * 100)}%")

    # Barra di avanzamento
    track = QRectF(STATUS_X, 262, WIDTH - 2 * STATUS_X, 6)
    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(QColor("#343a42"))
    p.drawRoundedRect(track, 3, 3)
    if fraction:
        done = QRectF(track.x(), track.y(), max(track.height(), track.width() * min(fraction, 1.0)), track.height())
        p.setBrush(QColor(ACCENT))
        p.drawRoundedRect(done, 3, 3)
    p.end()
    return pm


class StartupSplash(QSplashScreen):
    """Schermata di avvio: si aggiorna mentre si caricano i componenti, si chiude con la finestra."""

    def __init__(self, version: str):
        self._version = version
        self._dpr = QApplication.primaryScreen().devicePixelRatio() if QApplication.primaryScreen() else 1.0
        super().__init__(render_splash(0.0, "Avvio…", version, self._dpr))
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, False)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)

    def show_progress(self, fraction: float, status: str) -> None:
        self.setPixmap(render_splash(fraction, status, self._version, self._dpr))
        QApplication.processEvents()

    def load_modules(self) -> None:
        """Importa le librerie pesanti una alla volta, aggiornando la barra."""
        total = sum(w for *_, w in STEPS) + WINDOW_WEIGHT
        done = 0
        for module, label, weight in STEPS:
            self.show_progress(done / total, f"Caricamento: {label.lower()}…")
            try:
                importlib.import_module(module)
            except ImportError:
                pass  # componente facoltativo: l'app ne farà a meno o lo segnalerà più avanti
            done += weight
        self.show_progress(done / total, "Preparazione della finestra…")

    def mousePressEvent(self, event) -> None:  # noqa: N802 (API Qt)
        pass  # un clic non deve chiudere la schermata prima che la finestra sia pronta
