"""Carosello delle foto di una cartella: miniature, scorrimento e selezione di più foto da esportare."""

from __future__ import annotations

from pathlib import Path
from typing import Callable

from PySide6.QtCore import QItemSelectionModel, QModelIndex, QPointF, QRect, QRectF, QSize, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QImage, QPainter, QPen, QPixmap
from PySide6.QtWidgets import (
    QAbstractItemView,
    QApplication,
    QCheckBox,
    QFrame,
    QHBoxLayout,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QPushButton,
    QSizePolicy,
    QStyle,
    QStyledItemDelegate,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from ..io.thumbnail import make_thumbnail
from ..sidecar import has_settings
from .style import ACCENT, PRIMARY, TEXT_DIM, WARN
from .workers import Task

#: Lato lungo delle miniature, in pixel logici (calcolate al doppio per gli schermi ad alta densità).
THUMB = 112
_CELL = QSize(THUMB + 16, THUMB + 28)
_PATH = Qt.ItemDataRole.UserRole
_NAVIGATION_KEYS = (Qt.Key.Key_Left, Qt.Key.Key_Right, Qt.Key.Key_Home, Qt.Key.Key_End,
                    Qt.Key.Key_PageUp, Qt.Key.Key_PageDown)


def _no_modifiers() -> bool:
    mods = QApplication.keyboardModifiers()
    return not mods & (Qt.KeyboardModifier.ControlModifier | Qt.KeyboardModifier.ShiftModifier)


class _Delegate(QStyledItemDelegate):
    """Miniatura con il nome sotto; le foto selezionate hanno sfondo e spunta, quella aperta un bordo."""

    def __init__(self, strip: "Filmstrip"):
        super().__init__(strip)
        self._strip = strip

    def sizeHint(self, _option, _index) -> QSize:  # noqa: N802 (API Qt)
        return _CELL

    def paint(self, p: QPainter, option, index: QModelIndex) -> None:
        r = option.rect.adjusted(3, 3, -3, -3)
        selected = bool(option.state & QStyle.StateFlag.State_Selected)
        shown = index.data(_PATH) == self._strip.shown_path_text
        p.save()
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        if selected:
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(PRIMARY).darker(130))
            p.drawRoundedRect(QRectF(r), 5, 5)
        thumb_area = QRect(r.x() + 5, r.y() + 5, r.width() - 10, THUMB)
        pixmap = index.data(Qt.ItemDataRole.DecorationRole)
        if isinstance(pixmap, QPixmap) and not pixmap.isNull():
            size = pixmap.deviceIndependentSize().toSize().scaled(thumb_area.size(), Qt.AspectRatioMode.KeepAspectRatio)
            target = QRect(0, 0, size.width(), size.height())
            target.moveCenter(thumb_area.center())
            p.drawPixmap(target, pixmap)
        else:  # miniatura in arrivo: riquadro con il tipo di file
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(52, 52, 52))
            p.drawRoundedRect(QRectF(thumb_area.adjusted(10, 18, -10, -18)), 4, 4)
            p.setPen(QColor(TEXT_DIM))
            p.drawText(thumb_area, Qt.AlignmentFlag.AlignCenter, Path(index.data(_PATH)).suffix.upper().lstrip("."))
        font = QFont(option.font)
        font.setPointSizeF(8)
        p.setFont(font)
        text_rect = QRect(r.x() + 4, thumb_area.bottom() + 3, r.width() - 8, r.bottom() - thumb_area.bottom() - 3)
        name = p.fontMetrics().elidedText(index.data(Qt.ItemDataRole.DisplayRole), Qt.TextElideMode.ElideMiddle,
                                          text_rect.width())
        p.setPen(QColor("white") if selected or shown else QColor(TEXT_DIM))
        p.drawText(text_rect, Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignTop, name)
        if selected:  # spunta: si vede subito quali foto verranno esportate
            badge = QRectF(r.right() - 22, r.y() + 2, 18, 18)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(ACCENT))
            p.drawEllipse(badge)
            pen = QPen(QColor("white"), 2)
            pen.setCapStyle(Qt.PenCapStyle.RoundCap)
            p.setPen(pen)
            c = badge.center()
            p.drawPolyline([c + QPointF(-4.5, 0.5), c + QPointF(-1, 4), c + QPointF(4.5, -3.5)])
        if index.data(_PATH) in self._strip.edited:  # regolazioni salvate per questa foto
            p.setPen(QPen(QColor(20, 20, 20), 2))
            p.setBrush(QColor(WARN))
            p.drawEllipse(QRectF(thumb_area.left() + 5, thumb_area.bottom() - 17, 12, 12))
        if shown:
            p.setPen(QPen(QColor(ACCENT), 2))
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawRoundedRect(QRectF(r).adjusted(1, 1, -1, -1), 5, 5)
        p.restore()


class _List(QListWidget):
    navigated = Signal()  # spostamento con la tastiera senza Ctrl/Maiusc: si apre la foto

    def keyPressEvent(self, event) -> None:  # noqa: N802 (API Qt)
        super().keyPressEvent(event)
        mods = event.modifiers() & (Qt.KeyboardModifier.ControlModifier | Qt.KeyboardModifier.ShiftModifier)
        if event.key() in _NAVIGATION_KEYS and not mods:
            self.navigated.emit()


class Filmstrip(QFrame):
    """Striscia in basso con le foto della cartella aperta.

    Un clic apre la foto; Ctrl+clic e Maiusc+clic aggiungono foto alla selezione senza aprirle.
    """

    photoActivated = Signal(object)  # Path della foto da aprire
    selectionChanged = Signal()
    closeRequested = Signal()

    def __init__(self, run: Callable[[Task], None]):
        super().__init__()
        self.setObjectName("filmstrip")
        self._run = run
        self._folder: Path | None = None
        self._shown = ""
        #: Foto con regolazioni salvate (segnate da un punto giallo).
        self.edited: set[str] = set()
        self._generation = 0
        self._queue: list[QListWidgetItem] = []
        self._activate_timer = QTimer(self, singleShot=True, interval=180)
        self._activate_timer.timeout.connect(self._activate_current)

        v = QVBoxLayout(self)
        v.setContentsMargins(8, 4, 8, 6)
        v.setSpacing(2)
        header = QHBoxLayout()
        header.setSpacing(6)
        self.title = QLabel()
        self.title.setTextFormat(Qt.TextFormat.RichText)
        self.title.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
        header.addWidget(self.title, 1)
        self.select_all = QPushButton("Seleziona tutte")
        self.select_all.setObjectName("link")
        self.select_all.setToolTip("Seleziona tutte le foto della cartella (Ctrl+A nel carosello)")
        self.select_all.clicked.connect(self._select_all)
        self.select_none = QPushButton("Solo quella aperta")
        self.select_none.setObjectName("link")
        self.select_none.setToolTip("Toglie dalla selezione tutte le foto tranne quella aperta")
        self.select_none.clicked.connect(self._select_shown_only)
        header.addWidget(self.select_all)
        header.addWidget(self.select_none)
        self.sync = QCheckBox("Applica le modifiche a tutte le selezionate")
        self.sync.setToolTip(
            "Per le raffiche: ogni regolazione che cambi sulla foto aperta (esposizione, bilanciamento, toni, "
            "riduzione del rumore…) viene copiata anche sulle altre foto selezionate. Le altre regolazioni di "
            "ciascuna foto restano le sue."
        )
        self.sync.hide()
        header.addWidget(self.sync)
        self._header = header
        close = QToolButton()
        close.setText("✕")
        close.setToolTip("Chiudi il carosello")
        close.clicked.connect(self.closeRequested)
        self._close = close
        header.addWidget(close)
        v.addLayout(header)

        self.list = _List()
        self.list.setViewMode(QListWidget.ViewMode.IconMode)
        self.list.setFlow(QListWidget.Flow.LeftToRight)
        self.list.setWrapping(False)
        self.list.setMovement(QListWidget.Movement.Static)
        self.list.setResizeMode(QListWidget.ResizeMode.Adjust)
        self.list.setUniformItemSizes(True)
        self.list.setGridSize(_CELL)
        self.list.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.list.setHorizontalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        self.list.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.list.setItemDelegate(_Delegate(self))
        self.list.setFixedHeight(_CELL.height() + self.list.horizontalScrollBar().sizeHint().height() + 6)
        self.list.itemClicked.connect(self._on_clicked)
        self.list.navigated.connect(self._activate_timer.start)
        self.list.itemSelectionChanged.connect(self._on_selection)
        v.addWidget(self.list)
        self._update_title()

    # ------------------------------------------------------------------ contenuto

    def add_header_widget(self, widget: QWidget) -> None:
        """Aggiunge un pulsante nell'intestazione, prima della chiusura."""
        self._header.insertWidget(self._header.indexOf(self._close), widget)

    def set_photos(self, paths: list[Path], folder: Path | None) -> None:
        self._generation += 1
        self._queue = []
        self._folder = folder
        self.list.clear()
        self.edited = {str(p) for p in paths if has_settings(p)}
        for p in paths:
            item = QListWidgetItem(p.name)
            item.setData(_PATH, str(p))
            item.setToolTip(str(p))
            self.list.addItem(item)
            self._queue.append(item)
        self._update_title()
        self._load_next(self._generation)

    def clear(self) -> None:
        self.set_photos([], None)
        self._shown = ""

    def paths(self) -> list[Path]:
        return [Path(self.list.item(i).data(_PATH)) for i in range(self.list.count())]

    def selected_paths(self) -> list[Path]:
        """Foto selezionate, nell'ordine del carosello."""
        return [Path(self.list.item(i).data(_PATH)) for i in range(self.list.count())
                if self.list.item(i).isSelected()]

    def set_edited(self, paths, edited: bool = True) -> None:
        for path in paths:
            (self.edited.add if edited else self.edited.discard)(str(path))
        self.list.viewport().update()

    def sync_enabled(self) -> bool:
        return not self.sync.isHidden() and self.sync.isChecked()

    def contains(self, path: Path) -> bool:
        return self._item_for(path) is not None

    @property
    def shown_path_text(self) -> str:
        return self._shown

    def set_shown(self, path: Path | None) -> None:
        """Segna la foto aperta (aperta anche da altrove, es. dai recenti) senza emettere segnali."""
        self._shown = str(path) if path is not None else ""
        item = self._item_for(path) if path is not None else None
        if item is not None and not item.isSelected():
            self.list.blockSignals(True)
            self.list.clearSelection()
            item.setSelected(True)
            self.list.blockSignals(False)
            self._on_selection()
        if item is not None:
            self.list.blockSignals(True)
            self.list.setCurrentItem(item, QItemSelectionModel.SelectionFlag.NoUpdate)
            self.list.blockSignals(False)
            self.list.scrollToItem(item)
        self.list.viewport().update()

    def step(self, direction: int) -> None:
        """Foto precedente (-1) o successiva (+1), che viene aperta."""
        count = self.list.count()
        if not count:
            return
        current = self._item_for(Path(self._shown)) if self._shown else None
        row = self.list.row(current) if current is not None else -1
        row = min(max(0, row + direction), count - 1) if row >= 0 else 0
        item = self.list.item(row)
        self.list.setCurrentItem(item)  # seleziona solo questa
        self.list.scrollToItem(item)
        self._activate_timer.start()

    # ------------------------------------------------------------------ interazione

    def _item_for(self, path: Path | None) -> QListWidgetItem | None:
        if path is None:
            return None
        text = str(path)
        for i in range(self.list.count()):
            if self.list.item(i).data(_PATH) == text:
                return self.list.item(i)
        return None

    def _on_clicked(self, item: QListWidgetItem) -> None:
        if _no_modifiers():
            self._activate_timer.stop()
            self.photoActivated.emit(Path(item.data(_PATH)))

    def _activate_current(self) -> None:
        item = self.list.currentItem()
        if item is not None and item.data(_PATH) != self._shown:
            self.photoActivated.emit(Path(item.data(_PATH)))

    def _select_all(self) -> None:
        self.list.selectAll()

    def _select_shown_only(self) -> None:
        self.list.clearSelection()
        item = self._item_for(Path(self._shown)) if self._shown else None
        if item is not None:
            item.setSelected(True)

    def _on_selection(self) -> None:
        self._update_title()
        self.list.viewport().update()
        self.selectionChanged.emit()

    def _update_title(self) -> None:
        count = self.list.count()
        selected = len(self.list.selectedItems())
        self.sync.setVisible(selected > 1)
        where = self._folder.name if self._folder is not None else "Foto aperte"
        parts = [f"<b>{where}</b>", f"{count} foto"]
        if selected:
            parts.append(f"{selected} selezionat{'a' if selected == 1 else 'e'}")
        hint = "<span style='color:#9a9a9a'>clic: apri · Ctrl+clic, Maiusc+clic: più foto · ● regolata</span>"
        self.title.setText(" · ".join(parts) + " &nbsp; " + hint)
        if self._folder is not None:
            self.title.setToolTip(str(self._folder))

    # ------------------------------------------------------------------ miniature

    def _load_next(self, generation: int) -> None:
        """Una miniatura alla volta: l'anteprima della foto aperta non resta in coda dietro a tutte."""
        if generation != self._generation or not self._queue:
            return
        item = self._queue.pop(0)
        path = Path(item.data(_PATH))
        dpr = max(1.0, self.devicePixelRatioF())
        task = Task(make_thumbnail, path, int(THUMB * dpr))

        def done(rgb) -> None:
            if generation == self._generation:
                h, w = rgb.shape[:2]
                image = QImage(rgb.data, w, h, rgb.strides[0], QImage.Format.Format_RGB888).copy()
                pixmap = QPixmap.fromImage(image)
                pixmap.setDevicePixelRatio(dpr)
                item.setData(Qt.ItemDataRole.DecorationRole, pixmap)
            self._load_next(generation)

        task.signals.finished.connect(done)
        task.signals.failed.connect(lambda *_: self._load_next(generation))  # resta il riquadro col tipo
        task.signals.cancelled.connect(lambda: self._load_next(generation))
        self._run(task)


__all__ = ["Filmstrip"]
