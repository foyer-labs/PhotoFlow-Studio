"""Tema grafico: scuro e neutro come nei programmi di fotoritocco, con un solo colore d'accento.

Un'interfaccia scura e senza dominanti non altera la percezione di luminosità e colori della foto.
"""

from __future__ import annotations

import sys

from PySide6.QtCore import QLocale, QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QFont, QIcon, QIconEngine, QPainter, QPainterPath, QPalette, QPen, QPixmap
from PySide6.QtWidgets import QApplication, QWidget

ACCENT = "#4a8fd9"
OK = "#6fbf73"
WARN = "#e0b050"
TEXT = "#d4d4d4"
TEXT_DIM = "#9a9a9a"  # contrasto ≥ 4,5:1 sullo sfondo (WCAG AA)
#: Sfondo dei pulsanti principali: testo bianco con contrasto ≥ 4,5:1.
PRIMARY = "#3570b8"
PRIMARY_HOVER = "#3d7cc9"
CANVAS = QColor(30, 30, 30)

QSS = f"""
QWidget {{ color: {TEXT}; }}
QWidget:disabled {{ color: #6a6a6a; }}
QMainWindow, QDialog {{ background: #262626; }}
QToolTip {{ background: #333333; color: #e0e0e0; border: 1px solid #444; padding: 6px; }}
QMenuBar {{ background: #202020; }}
QMenuBar::item:selected {{ background: #353535; }}
QMenu {{ background: #2b2b2b; border: 1px solid #3a3a3a; }}
QMenu::item:selected {{ background: {PRIMARY}; color: white; }}
QMenu::separator {{ height: 1px; background: #3a3a3a; margin: 4px 8px; }}
QToolBar {{ background: #202020; border: none; border-bottom: 1px solid #111; spacing: 4px; padding: 4px 6px; }}
QToolButton {{ padding: 4px 8px; border-radius: 4px; }}
QToolButton:hover {{ background: #353535; }}
QToolButton:checked {{ background: #3a3a3a; }}
QToolButton#primary {{ background: {PRIMARY}; color: white; padding: 5px 16px; font-weight: 600; }}
QToolButton#primary:hover {{ background: {PRIMARY_HOVER}; }}
QToolButton#primary:disabled {{ background: #3a3a3a; color: #777; }}
QPushButton {{ background: #353535; border: 1px solid #444; border-radius: 4px; padding: 5px 12px; }}
QPushButton:hover {{ background: #404040; }}
QPushButton:disabled {{ color: #666; border-color: #333; }}
QPushButton#primary {{ background: {PRIMARY}; border-color: {PRIMARY}; color: white; font-weight: 600; }}
QPushButton#primary:hover {{ background: {PRIMARY_HOVER}; }}
QPushButton#link {{ background: transparent; border: none; color: {ACCENT}; padding: 2px 4px; text-align: left; }}
QPushButton#link:hover {{ text-decoration: underline; }}
QComboBox, QSpinBox, QDoubleSpinBox, QLineEdit {{
    background: #1e1e1e; border: 1px solid #5c5c5c; border-radius: 4px; padding: 2px 6px; min-height: 20px;
}}
QAbstractSpinBox::up-button, QAbstractSpinBox::down-button {{ subcontrol-origin: border; width: 0px; height: 0px; border: none; }}
QComboBox:focus, QSpinBox:focus, QDoubleSpinBox:focus, QLineEdit:focus {{ border-color: {ACCENT}; }}
QComboBox:disabled, QSpinBox:disabled, QDoubleSpinBox:disabled, QLineEdit:disabled {{ color: #5c5c5c; border-color: #2e2e2e; }}
QComboBox QAbstractItemView {{ background: #2b2b2b; selection-background-color: {ACCENT}; }}
QSlider::groove:horizontal {{ height: 2px; background: #6a6a6a; }}
QSlider::sub-page:horizontal {{ background: #a0a0a0; }}
QSlider::handle:horizontal {{ width: 12px; height: 12px; margin: -5px 0; border-radius: 6px; background: #d0d0d0; }}
QSlider::handle:horizontal:hover {{ background: #ffffff; }}
QSlider::handle:horizontal:disabled {{ background: #555; }}
QCheckBox::indicator {{ width: 14px; height: 14px; border: 1px solid #7a7a7a; border-radius: 3px; background: #1e1e1e; }}
QCheckBox::indicator:hover {{ border-color: #a0a0a0; }}
QCheckBox::indicator:checked {{ background: {PRIMARY}; border-color: {PRIMARY}; image: url(%CHECK%); }}
QCheckBox::indicator:disabled {{ border-color: #444; background: #232323; }}
QCheckBox::indicator:checked:disabled {{ background: #3a3a3a; border-color: #3a3a3a; }}
QGroupBox {{ border: none; border-top: 1px solid #3a3a3a; margin-top: 16px; padding-top: 12px; font-weight: 600; }}
QGroupBox::title {{ subcontrol-origin: margin; left: 0; padding: 0 0 4px 0; color: #bdbdbd; }}
QGroupBox#sub {{ border-top: none; border-left: 2px solid #3a3a3a; margin-top: 8px; margin-left: 2px; padding-left: 8px; font-weight: 500; }}
QGroupBox#sub::title {{ color: #9a9a9a; left: 8px; }}
QLabel#hint {{ color: {TEXT_DIM}; font-size: 8pt; }}
QLabel#title {{ font-size: 11pt; font-weight: 600; }}
QLabel#explain {{ color: #b8b8b8; background: #1e1e1e; border-top: 1px solid #333; padding: 8px; }}
QFrame#banner {{ background: #23384f; border-bottom: 1px solid #2f4d6d; }}
QFrame#banner QLabel {{ color: #e8f0fa; }}
QFrame#welcome {{ background: #1e1e1e; }}
QStatusBar {{ background: #1e1e1e; border-top: 1px solid #111; color: #9a9a9a; }}
QStatusBar::item {{ border: none; }}
QScrollArea {{ border: none; }}
QDockWidget {{ titlebar-close-icon: none; }}
QProgressBar {{ background: #1e1e1e; border: 1px solid #3a3a3a; border-radius: 3px; text-align: center; height: 12px; }}
QProgressBar::chunk {{ background: {ACCENT}; border-radius: 2px; }}
QListWidget {{ background: #1e1e1e; border: 1px solid #3a3a3a; }}
QLabel#kbd {{
    color: #c8c8c8; background: #2e2e2e; border: 1px solid #4a4a4a; border-bottom: 2px solid #4a4a4a;
    border-radius: 3px; padding: 0px 5px; font-size: 8pt; font-family: "Segoe UI", sans-serif;
}}
QToolButton#primary QLabel {{ color: white; background: transparent; }}
QToolButton#primary QLabel#kbd {{ color: white; background: rgba(255,255,255,0.12); border-color: rgba(255,255,255,0.45); }}
QLabel[off="true"] {{ color: #6a6a6a; }}
QLabel#kbd[off="true"] {{ color: #6a6a6a; border-color: #3a3a3a; }}
QToolButton#primary QLabel[off="true"] {{ color: #8a8a8a; }}
QToolButton#primary QLabel#kbd[off="true"] {{ color: #8a8a8a; background: transparent; border-color: #4a4a4a; }}
QFrame#keybar {{ background: #1b1b1b; border-top: 1px solid #111; }}
QFrame#keybar QLabel#keytext {{ color: #9a9a9a; font-size: 8pt; }}
"""


def apply_theme(app: QApplication) -> None:
    app.setStyle("Fusion")
    palette = QPalette()
    colors = {
        QPalette.ColorRole.Window: "#262626",
        QPalette.ColorRole.WindowText: TEXT,
        QPalette.ColorRole.Base: "#1e1e1e",
        QPalette.ColorRole.AlternateBase: "#2b2b2b",
        QPalette.ColorRole.Button: "#333333",
        QPalette.ColorRole.ButtonText: "#e0e0e0",
        QPalette.ColorRole.Text: TEXT,
        QPalette.ColorRole.Highlight: ACCENT,
        QPalette.ColorRole.HighlightedText: "#ffffff",
        QPalette.ColorRole.ToolTipBase: "#333333",
        QPalette.ColorRole.ToolTipText: "#e0e0e0",
        QPalette.ColorRole.Mid: "#3a3a3a",
        QPalette.ColorRole.Dark: "#1a1a1a",
        QPalette.ColorRole.Link: ACCENT,
        QPalette.ColorRole.PlaceholderText: "#6a6a6a",
    }
    for role, color in colors.items():
        palette.setColor(role, QColor(color))
    for role in (QPalette.ColorRole.Text, QPalette.ColorRole.WindowText, QPalette.ColorRole.ButtonText):
        palette.setColor(QPalette.ColorGroup.Disabled, role, QColor("#6a6a6a"))
    app.setPalette(palette)
    app.setStyleSheet(QSS.replace("%CHECK%", _check_mark_file()))
    font = QFont("Segoe UI" if sys.platform == "win32" else app.font().family(), 9)
    app.setFont(font)
    # Virgola decimale e formati italiani nelle caselle numeriche.
    QLocale.setDefault(QLocale(QLocale.Language.Italian, QLocale.Country.Italy))
    app.setWindowIcon(app_icon())


def _check_mark_file() -> str:
    """Segno di spunta per le caselle (il QSS vuole un file): disegnato una volta in una cartella temporanea."""
    import tempfile
    from pathlib import Path

    path = Path(tempfile.gettempdir()) / "denoiser-theme" / "spunta.png"
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        pm = QPixmap(48, 48)
        pm.fill(Qt.GlobalColor.transparent)
        p = QPainter(pm)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        pen = QPen(QColor("white"), 6)
        pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
        p.setPen(pen)
        p.drawPolyline([QPointF(11, 25), QPointF(20, 34), QPointF(37, 14)])
        p.end()
        pm.save(str(path))
    except OSError:
        return ""
    return path.as_posix()


def dark_title_bar(widget: QWidget) -> None:
    """Barra del titolo scura su Windows 10/11 (nessun effetto altrove)."""
    if sys.platform != "win32":
        return
    try:
        import ctypes

        hwnd = int(widget.winId())
        value = ctypes.c_int(1)
        for attribute in (20, 19):  # DWMWA_USE_IMMERSIVE_DARK_MODE (nuovo e vecchio valore)
            if ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, attribute, ctypes.byref(value), 4) == 0:
                break
    except Exception:
        pass


# ---------------------------------------------------------------------------- icone

class _VectorIcon(QIconEngine):
    """Icona vettoriale: disegnata alla dimensione e densità richieste, sempre nitida."""

    def __init__(self, name: str, color: str | None):
        super().__init__()
        self._name = name
        self._color = color

    def clone(self) -> "QIconEngine":
        return _VectorIcon(self._name, self._color)

    def paint(self, painter: QPainter, rect, mode, state) -> None:  # noqa: ARG002
        painter.save()
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        color = QColor(self._color or TEXT)
        if mode == QIcon.Mode.Disabled:
            color = QColor("#6a6a6a")
        pen = QPen(color, 2.0)
        pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
        painter.translate(rect.x(), rect.y())
        painter.scale(rect.width() / 24, rect.height() / 24)
        painter.setPen(pen)
        _draw_icon(painter, self._name)
        painter.restore()

    def scaledPixmap(self, size, mode, state, scale: float) -> QPixmap:  # noqa: N802 (API Qt)
        pm = QPixmap(max(1, round(size.width() * scale)), max(1, round(size.height() * scale)))
        pm.fill(Qt.GlobalColor.transparent)
        p = QPainter(pm)
        self.paint(p, pm.rect(), mode, state)
        p.end()
        pm.setDevicePixelRatio(scale)
        return pm

    def pixmap(self, size, mode, state) -> QPixmap:
        return self.scaledPixmap(size, mode, state, 1.0)


def _draw_icon(p: QPainter, name: str) -> None:
    """Disegna l'icona ``name`` in un quadrato 24×24."""
    path = QPainterPath()
    if name == "open":
        path.moveTo(3, 7); path.lineTo(9, 7); path.lineTo(11, 9); path.lineTo(21, 9)
        path.lineTo(21, 19); path.lineTo(3, 19); path.closeSubpath()
        p.drawPath(path)
    elif name == "batch":
        p.drawRect(QRectF(7, 3, 13, 11))
        p.drawPolyline([QPointF(4, 7), QPointF(4, 17), QPointF(16, 17)])
        p.drawPolyline([QPointF(1.5, 10), QPointF(1.5, 20.5), QPointF(13, 20.5)])
    elif name == "export":
        p.drawLine(QPointF(12, 3), QPointF(12, 15))
        p.drawPolyline([QPointF(7, 10), QPointF(12, 15), QPointF(17, 10)])
        p.drawPolyline([QPointF(4, 15), QPointF(4, 20), QPointF(20, 20), QPointF(20, 15)])
    elif name == "fit":
        for a, b, c in (((3, 8), (3, 3), (8, 3)), ((16, 3), (21, 3), (21, 8)), ((21, 16), (21, 21), (16, 21)), ((8, 21), (3, 21), (3, 16))):
            p.drawPolyline([QPointF(*a), QPointF(*b), QPointF(*c)])
    elif name == "zoom100":
        p.drawEllipse(QRectF(3, 3, 13, 13))
        p.drawLine(QPointF(14, 14), QPointF(21, 21))
        p.drawLine(QPointF(9.5, 6.5), QPointF(9.5, 12.5))  # «1»: pixel reali
        p.drawLine(QPointF(8, 8), QPointF(9.5, 6.5))
    elif name == "compare":
        p.drawRect(QRectF(3, 5, 18, 14))
        p.drawLine(QPointF(12, 3), QPointF(12, 21))
    elif name == "help":
        p.drawEllipse(QRectF(3, 3, 18, 18))
        path.moveTo(9.5, 9.5); path.cubicTo(9.5, 6, 14.5, 6, 14.5, 9.5); path.cubicTo(14.5, 12, 12, 12, 12, 14.5)
        p.drawPath(path)
        p.drawPoint(QPointF(12, 17.5))
    elif name == "undo":
        path.moveTo(9, 6); path.lineTo(4, 11); path.lineTo(9, 16)
        p.drawPath(path)
        p.drawArc(QRectF(4, 6, 16, 14), 90 * 16, -180 * 16)
    elif name == "redo":
        path.moveTo(15, 6); path.lineTo(20, 11); path.lineTo(15, 16)
        p.drawPath(path)
        p.drawArc(QRectF(4, 6, 16, 14), 90 * 16, 180 * 16)
    elif name == "reset":
        p.drawArc(QRectF(4, 4, 16, 16), 60 * 16, 300 * 16)
        p.drawPolyline([QPointF(15, 2), QPointF(18, 5.5), QPointF(14, 7)])
    elif name == "download":
        p.drawLine(QPointF(12, 3), QPointF(12, 16))
        p.drawPolyline([QPointF(7, 11), QPointF(12, 16), QPointF(17, 11)])
        p.drawLine(QPointF(4, 20), QPointF(20, 20))


def icon(name: str, color: str | None = None) -> QIcon:
    """Icone semplici disegnate al volo (nessun file esterno)."""
    return QIcon(_VectorIcon("open" if name == "folder" else name, color))


def app_icon() -> QIcon:
    """Icona dell'applicazione: un obiettivo con metà della grana rimossa."""
    result = QIcon()
    for size in (16, 24, 32, 48, 64, 128, 256):
        pm = QPixmap(size, size)
        pm.fill(Qt.GlobalColor.transparent)
        p = QPainter(pm)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        s = size / 64
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor("#2a2f36"))
        p.drawRoundedRect(QRectF(2 * s, 2 * s, 60 * s, 60 * s), 14 * s, 14 * s)
        p.setBrush(QColor(ACCENT))
        p.drawEllipse(QRectF(12 * s, 12 * s, 40 * s, 40 * s))
        p.setBrush(QColor("#1b2430"))
        p.drawEllipse(QRectF(19 * s, 19 * s, 26 * s, 26 * s))
        # metà sinistra con "grana", metà destra pulita
        import random

        rnd = random.Random(7)
        p.setBrush(QColor(255, 255, 255, 170))
        for _ in range(int(40 * s) + 6):
            x, y = rnd.uniform(21, 31.5), rnd.uniform(21, 43)
            if (x - 32) ** 2 + (y - 32) ** 2 < 12.5**2:
                p.drawEllipse(QPointF(x * s, y * s), 0.9 * s, 0.9 * s)
        p.setPen(QPen(QColor("white"), max(1.0, 1.6 * s)))
        p.drawLine(QPointF(32 * s, 17 * s), QPointF(32 * s, 47 * s))
        p.end()
        result.addPixmap(pm)
    return result
