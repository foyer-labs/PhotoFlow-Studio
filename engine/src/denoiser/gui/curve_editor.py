"""Curva dei toni modificabile, disegnata sopra l'istogramma della foto."""

from __future__ import annotations

import numpy as np
from PySide6.QtCore import QPointF, QRectF, QSize, Qt, Signal
from PySide6.QtGui import QColor, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QSizePolicy, QWidget

from ..develop import monotone_curve
from ..settings import IDENTITY_CURVE, MAX_CURVE_POINTS
from .style import ACCENT, TEXT_DIM

_RADIUS = 5.0
_GRAB = 9.0
#: Distanza minima in orizzontale tra due punti della curva.
_MIN_GAP = 0.02
#: Quota di pixel neri o bianchi oltre la quale si segnalano ombre o alte luci tagliate.
_CLIP_WARNING = 0.002


class CurveEditor(QWidget):
    """Istogramma RGB della foto sviluppata e curva dei toni.

    Clic su un punto vuoto: aggiunge un punto. Trascina: sposta. Doppio clic su un punto (o
    trascinarlo fuori dal riquadro): lo toglie. I punti agli estremi si spostano solo in verticale.
    """

    curveChanged = Signal(object)  # tuple di punti, durante il trascinamento
    curveEdited = Signal(object)  # tuple di punti, al rilascio (per annulla/ripeti)

    def __init__(self, parent: QWidget | None = None):
        super().__init__(parent)
        self.setMinimumHeight(150)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.CrossCursor)
        self._points: list[list[float]] = [list(p) for p in IDENTITY_CURVE]
        self._histogram: np.ndarray | None = None
        self._drag: int | None = None
        self._hover: int | None = None
        self._outside = False
        self.setToolTip(
            "Curva dei toni sopra l'istogramma della foto (rosso, verde, blu). A sinistra le ombre, a destra le "
            "alte luci: alzare la curva schiarisce quei toni, abbassarla li scurisce. Clic per aggiungere un punto, "
            "trascina per spostarlo, doppio clic per toglierlo. I triangoli in alto si accendono quando ombre o "
            "alte luci sono tagliate (nero o bianco puro)."
        )

    def sizeHint(self) -> QSize:  # noqa: N802 (API Qt)
        return QSize(320, 170)

    # ------------------------------------------------------------------ dati

    def points(self) -> tuple[tuple[float, float], ...]:
        return tuple((round(x, 4), round(y, 4)) for x, y in self._points)

    def set_points(self, points) -> None:
        pts = sorted([float(x), float(y)] for x, y in points)
        if len(pts) < 2:
            pts = [list(p) for p in IDENTITY_CURVE]
        self._points = pts
        self._drag = None
        self.update()

    def set_histogram(self, histogram: np.ndarray | None) -> None:
        """``histogram``: (4, bins) rosso, verde, blu, luminanza; None = nessuna foto."""
        self._histogram = histogram
        self.update()

    # ------------------------------------------------------------------ geometria

    def _area(self) -> QRectF:
        return QRectF(8, 14, self.width() - 16, self.height() - 22)

    def _to_screen(self, x: float, y: float) -> QPointF:
        a = self._area()
        return QPointF(a.left() + x * a.width(), a.bottom() - y * a.height())

    def _to_curve(self, pos: QPointF) -> tuple[float, float]:
        a = self._area()
        return (pos.x() - a.left()) / max(a.width(), 1), (a.bottom() - pos.y()) / max(a.height(), 1)

    def _point_at(self, pos: QPointF) -> int | None:
        best, best_d = None, _GRAB
        for i, (x, y) in enumerate(self._points):
            p = self._to_screen(x, y)
            d = ((p.x() - pos.x()) ** 2 + (p.y() - pos.y()) ** 2) ** 0.5
            if d <= best_d:
                best, best_d = i, d
        return best

    # ------------------------------------------------------------------ disegno

    def paintEvent(self, _event) -> None:  # noqa: N802 (API Qt)
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        a = self._area()
        p.fillRect(self.rect(), QColor(30, 30, 30))
        p.fillRect(a, QColor(22, 22, 22))
        p.setPen(QPen(QColor(255, 255, 255, 22), 1))
        for i in range(1, 4):
            x = a.left() + a.width() * i / 4
            y = a.top() + a.height() * i / 4
            p.drawLine(QPointF(x, a.top()), QPointF(x, a.bottom()))
            p.drawLine(QPointF(a.left(), y), QPointF(a.right(), y))
        self._draw_histogram(p, a)
        p.setPen(QPen(QColor(255, 255, 255, 50), 1, Qt.PenStyle.DashLine))
        p.drawLine(self._to_screen(0, 0), self._to_screen(1, 1))
        xs = np.linspace(0.0, 1.0, max(64, int(a.width())))
        ys = monotone_curve(self._points, xs)
        path = QPainterPath(self._to_screen(xs[0], ys[0]))
        for x, y in zip(xs[1:], ys[1:]):
            path.lineTo(self._to_screen(float(x), float(y)))
        p.setPen(QPen(QColor(235, 235, 235), 1.6))
        p.drawPath(path)
        for i, (x, y) in enumerate(self._points):
            removing = i == self._drag and self._outside
            p.setPen(QPen(QColor("white"), 1.2))
            p.setBrush(QColor(ACCENT) if i in (self._drag, self._hover) and not removing else QColor(40, 40, 40))
            if removing:
                p.setOpacity(0.35)
            p.drawEllipse(self._to_screen(x, y), _RADIUS, _RADIUS)
            p.setOpacity(1.0)
        p.setPen(QColor(TEXT_DIM))
        font = p.font()
        font.setPointSizeF(7.5)
        p.setFont(font)
        p.drawText(QRectF(a.left(), 0, a.width() / 2, 13), Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                   "ombre")
        p.drawText(QRectF(a.center().x(), 0, a.width() / 2, 13),
                   Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter, "alte luci")
        self._draw_clipping(p, a)

    def _draw_histogram(self, p: QPainter, a: QRectF) -> None:
        h = self._histogram
        if h is None or h.shape[1] < 2:
            return
        # Radice quadrata dei conteggi: i picchi non schiacciano il resto (come nei programmi di fotoritocco).
        counts = np.sqrt(h[:3].astype(float))
        top = max(float(np.percentile(counts[:, 1:-1], 99.5)) if counts.shape[1] > 2 else 0.0, 1e-9)
        bins = h.shape[1]
        p.setPen(Qt.PenStyle.NoPen)
        p.setCompositionMode(QPainter.CompositionMode.CompositionMode_Plus)
        for ch, color in enumerate((QColor(200, 60, 60, 150), QColor(60, 180, 70, 150), QColor(70, 110, 220, 150))):
            path = QPainterPath(QPointF(a.left(), a.bottom()))
            for i in range(bins):
                x = a.left() + a.width() * (i + 0.5) / bins
                y = a.bottom() - a.height() * min(counts[ch, i] / top, 1.0)
                path.lineTo(QPointF(x, y))
            path.lineTo(QPointF(a.right(), a.bottom()))
            path.closeSubpath()
            p.setBrush(color)
            p.drawPath(path)
        p.setCompositionMode(QPainter.CompositionMode.CompositionMode_SourceOver)

    def _draw_clipping(self, p: QPainter, a: QRectF) -> None:
        h = self._histogram
        if h is None:
            return
        total = max(float(h[3].sum()), 1.0)
        for clipped, x, direction in ((h[:3, 0].max() / total, a.left() + 2, 1), (h[:3, -1].max() / total, a.right() - 2, -1)):
            on = clipped > _CLIP_WARNING
            tri = QPainterPath(QPointF(x, 2))
            tri.lineTo(QPointF(x + 9 * direction, 2))
            tri.lineTo(QPointF(x, 11))
            tri.closeSubpath()
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor("white") if on else QColor(255, 255, 255, 40))
            p.drawPath(tri)

    # ------------------------------------------------------------------ mouse

    def mousePressEvent(self, event) -> None:  # noqa: N802 (API Qt)
        if event.button() != Qt.MouseButton.LeftButton:
            return
        pos = event.position()
        index = self._point_at(pos)
        if index is None:
            x, _y = self._to_curve(pos)
            if len(self._points) >= MAX_CURVE_POINTS or not 0 < x < 1:
                return
            if any(abs(px - x) < _MIN_GAP for px, _ in self._points):
                return
            y = float(monotone_curve(self._points, np.array([x]))[0])
            self._points.append([x, y])
            self._points.sort()
            index = next(i for i, (px, _py) in enumerate(self._points) if px == x)
        self._drag = index
        self._outside = False
        self._move(pos)

    def mouseMoveEvent(self, event) -> None:  # noqa: N802 (API Qt)
        pos = event.position()
        if self._drag is None:
            hover = self._point_at(pos)
            if hover != self._hover:
                self._hover = hover
                self.update()
            return
        self._move(pos)

    def _move(self, pos: QPointF) -> None:
        i = self._drag
        if i is None:
            return
        x, y = self._to_curve(pos)
        last = len(self._points) - 1
        inner = 0 < i < last
        # Un punto interno trascinato lontano dal riquadro viene tolto al rilascio.
        self._outside = inner and (y < -0.15 or y > 1.15 or x < -0.1 or x > 1.1)
        y = min(max(y, 0.0), 1.0)
        if inner:
            lo = self._points[i - 1][0] + _MIN_GAP
            hi = self._points[i + 1][0] - _MIN_GAP
            x = min(max(x, lo), hi)
        else:
            x = self._points[i][0]  # gli estremi si spostano solo in verticale
        self._points[i] = [x, y]
        self.update()
        self.curveChanged.emit(self._visible_points())

    def _visible_points(self) -> tuple[tuple[float, float], ...]:
        pts = [p for i, p in enumerate(self._points) if not (i == self._drag and self._outside)]
        return tuple((round(x, 4), round(y, 4)) for x, y in pts)

    def mouseReleaseEvent(self, _event) -> None:  # noqa: N802 (API Qt)
        if self._drag is None:
            return
        if self._outside:
            del self._points[self._drag]
        self._drag = None
        self._outside = False
        self.update()
        self.curveEdited.emit(self.points())

    def mouseDoubleClickEvent(self, event) -> None:  # noqa: N802 (API Qt)
        index = self._point_at(event.position())
        if index is not None and 0 < index < len(self._points) - 1:
            del self._points[index]
            self._drag = None
            self.update()
            self.curveEdited.emit(self.points())

    def leaveEvent(self, _event) -> None:  # noqa: N802 (API Qt)
        if self._hover is not None:
            self._hover = None
            self.update()

    def reset(self) -> None:
        self.set_points(IDENTITY_CURVE)
        self.curveEdited.emit(self.points())


__all__ = ["CurveEditor"]
