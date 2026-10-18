"""Controlli riutilizzabili del pannello delle impostazioni."""

from __future__ import annotations

from typing import Any, Callable

from PySide6.QtCore import QEvent, QObject, QSize, Qt, Signal
from PySide6.QtGui import QAction, QIcon, QKeySequence
from PySide6.QtWidgets import (
    QAbstractSpinBox,
    QCheckBox,
    QComboBox,
    QDoubleSpinBox,
    QFormLayout,
    QFrame,
    QHBoxLayout,
    QLabel,
    QSizePolicy,
    QSlider,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from ..algorithms import Param

_SLIDER_STEPS = 1000
#: Larghezza comune della colonna delle etichette: tutti i gruppi restano allineati.
LABEL_WIDTH = 128


class WheelGuard(QObject):
    """La rotellina cambia un valore solo se il controllo ha il fuoco: altrimenti scorre il pannello."""

    def eventFilter(self, obj: QObject, event: QEvent) -> bool:  # noqa: N802 (API Qt)
        if event.type() == QEvent.Type.Wheel and isinstance(obj, QWidget) and not obj.hasFocus():
            event.ignore()
            return True
        return False


_WHEEL_GUARD = WheelGuard()


def guard_wheel(widget: QWidget) -> None:
    widget.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
    widget.installEventFilter(_WHEEL_GUARD)


class ExplainFilter(QObject):
    """Mostra la spiegazione (tooltip) del controllo sotto il mouse in un riquadro fisso."""

    def __init__(self, show: Callable[[str], None]):
        super().__init__()
        self._show = show

    def eventFilter(self, obj: QObject, event: QEvent) -> bool:  # noqa: N802
        if event.type() in (QEvent.Type.Enter, QEvent.Type.FocusIn) and isinstance(obj, QWidget):
            widget: QWidget | None = obj
            while widget is not None and not widget.toolTip():
                widget = widget.parentWidget()
            if widget is not None:
                self._show(widget.toolTip())
        return False


def form_label(text: str) -> QLabel:
    label = QLabel(text)
    label.setFixedWidth(LABEL_WIDTH)
    label.setWordWrap(True)
    return label


def make_form(parent: QWidget | None = None) -> QFormLayout:
    form = QFormLayout(parent)
    form.setLabelAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)
    form.setHorizontalSpacing(10)
    form.setVerticalSpacing(6)
    form.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow)
    return form


def hint_label(text: str = "") -> QLabel:
    label = QLabel(text)
    label.setObjectName("hint")
    label.setWordWrap(True)
    return label


class MarkedSlider(QSlider):
    """Cursore orizzontale con una tacca fissa (es. «1 = rumore misurato» a metà corsa).

    Con lo stile del tema Qt non disegna le tacche: la si disegna qui.
    """

    def __init__(self):
        super().__init__(Qt.Orientation.Horizontal)
        self.mark: int | None = None

    def paintEvent(self, event) -> None:  # noqa: N802 (API Qt)
        if self.mark is not None:
            from PySide6.QtGui import QColor, QPainter

            p = QPainter(self)
            handle = 12  # larghezza della maniglia nel tema
            span = max(1, self.maximum() - self.minimum())
            x = round(handle / 2 + (self.mark - self.minimum()) / span * (self.width() - handle)) + 0.5
            mid = self.height() / 2
            p.setPen(QColor("#9a9a9a"))
            p.drawLine(int(x), int(mid - 7), int(x), int(mid - 4))
            p.drawLine(int(x), int(mid + 4), int(x), int(mid + 7))
            p.end()
        super().paintEvent(event)


class FloatControl(QWidget):
    """Cursore + casella numerica collegati, con didascalia opzionale.

    Con ``center`` il valore ``center`` cade a metà del cursore (scala non lineare): utile per
    le forze, dove 1 = «rumore misurato» deve essere il punto di riferimento visivo.
    Doppio clic sul cursore: torna al valore predefinito.
    """

    valueChanged = Signal(float)

    def __init__(
        self,
        minimum: float,
        maximum: float,
        step: float,
        value: float,
        decimals: int = 2,
        default: float | None = None,
        center: float | None = None,
        hint: str = "",
        suffix: str = "",
    ):
        super().__init__()
        self._min, self._max = minimum, maximum
        self._center = center
        self._default = value if default is None else default
        self.slider = MarkedSlider()
        self.slider.setRange(0, _SLIDER_STEPS)
        if center is not None:
            self.slider.mark = _SLIDER_STEPS // 2
        self.spin = QDoubleSpinBox()
        self.spin.setRange(minimum, maximum)
        self.spin.setSingleStep(step)
        self.spin.setDecimals(decimals)
        self.spin.setKeyboardTracking(False)
        self.spin.setButtonSymbols(QAbstractSpinBox.ButtonSymbols.NoButtons)
        self.spin.setAlignment(Qt.AlignmentFlag.AlignRight)
        self.spin.setFixedWidth(64 if not suffix else 78)
        if suffix:
            self.spin.setSuffix(suffix)
        for w in (self.slider, self.spin):
            guard_wheel(w)
        self.slider.installEventFilter(self)
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.addWidget(self.slider, 1)
        row.addWidget(self.spin)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(1)
        layout.addLayout(row)
        self.hint = hint_label(hint)
        self.hint.setVisible(bool(hint))
        layout.addWidget(self.hint)
        self.set_value(value)
        self.slider.valueChanged.connect(self._from_slider)
        self.spin.valueChanged.connect(self._from_spin)

    # ------------------------------------------------------------------ scala

    def _to_pos(self, value: float) -> int:
        lo, hi, c = self._min, self._max, self._center
        if c is None or not (lo < c < hi):
            frac = (value - lo) / ((hi - lo) or 1.0)
        elif value <= c:
            frac = 0.5 * (value - lo) / (c - lo)
        else:
            frac = 0.5 + 0.5 * (value - c) / (hi - c)
        return int(round(min(max(frac, 0.0), 1.0) * _SLIDER_STEPS))

    def _from_pos(self, pos: int) -> float:
        frac = pos / _SLIDER_STEPS
        lo, hi, c = self._min, self._max, self._center
        if c is None or not (lo < c < hi):
            return lo + (hi - lo) * frac
        if frac <= 0.5:
            return lo + (c - lo) * frac * 2
        return c + (hi - c) * (frac - 0.5) * 2

    # ------------------------------------------------------------------ API

    def value(self) -> float:
        return float(self.spin.value())

    def set_value(self, value: float) -> None:
        for w in (self.slider, self.spin):
            w.blockSignals(True)
        self.spin.setValue(value)
        self.slider.setValue(self._to_pos(self.spin.value()))
        for w in (self.slider, self.spin):
            w.blockSignals(False)

    def set_maximum(self, maximum: float) -> None:
        """Cambia il massimo (es. 1 per le reti «cieche»), limitando il valore se serve."""
        if maximum == self._max:
            return
        self._max = maximum
        self.spin.setMaximum(maximum)
        if self._center is not None:
            self.slider.mark = None if self._center >= maximum else _SLIDER_STEPS // 2
            self.slider.update()
        old = self.value()
        self.set_value(min(old, maximum))
        if self.value() != old:
            self.valueChanged.emit(self.value())

    def set_hint(self, text: str) -> None:
        self.hint.setText(text)
        self.hint.setVisible(bool(text))

    def reset(self) -> None:
        if self.value() != self._default:
            self.set_value(min(self._default, self._max))
            self.valueChanged.emit(self.value())

    def setToolTip(self, text: str) -> None:  # noqa: N802 (API Qt)
        super().setToolTip(text)
        self.slider.setToolTip(text)
        self.spin.setToolTip(text)

    def eventFilter(self, obj: QObject, event: QEvent) -> bool:  # noqa: N802
        if obj is self.slider and event.type() == QEvent.Type.MouseButtonDblClick:
            self.reset()
            return True
        return False

    def _from_slider(self, pos: int) -> None:
        self.spin.blockSignals(True)
        self.spin.setValue(self._from_pos(pos))
        self.spin.blockSignals(False)
        self.valueChanged.emit(self.value())

    def _from_spin(self, value: float) -> None:
        self.slider.blockSignals(True)
        self.slider.setValue(self._to_pos(value))
        self.slider.blockSignals(False)
        self.valueChanged.emit(float(value))


def make_combo(choices: dict[str, str], current: Any, tooltips: dict[str, str] | None = None) -> QComboBox:
    combo = QComboBox()
    combo.setSizeAdjustPolicy(QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon)
    combo.setMinimumContentsLength(8)
    for key, label in choices.items():
        combo.addItem(label, key)
        if tooltips and key in tooltips:
            combo.setItemData(combo.count() - 1, tooltips[key], Qt.ItemDataRole.ToolTipRole)
    set_combo(combo, current)
    guard_wheel(combo)
    return combo


def set_combo(combo: QComboBox, key: Any) -> None:
    index = combo.findData(key)
    if index >= 0:
        combo.blockSignals(True)
        combo.setCurrentIndex(index)
        combo.blockSignals(False)


class ParamForm(QWidget):
    """Costruisce automaticamente i controlli per i parametri di un algoritmo."""

    changed = Signal()

    def __init__(self, params: tuple[Param, ...], values: dict[str, Any]):
        super().__init__()
        self._params = params
        self._widgets: dict[str, QWidget] = {}
        layout = make_form(self)
        layout.setContentsMargins(0, 0, 0, 0)
        for p in params:
            value = values.get(p.key, p.default)
            if p.kind == "choice":
                w = make_combo(p.choices or {}, str(value))
                w.currentIndexChanged.connect(lambda *_: self.changed.emit())
            elif p.kind == "bool":
                w = QCheckBox()
                w.setChecked(bool(value))
                w.toggled.connect(lambda *_: self.changed.emit())
            else:
                decimals = 0 if p.kind == "int" else 2
                w = FloatControl(
                    float(p.minimum or 0), float(p.maximum or 1), float(p.step or (1 if p.kind == "int" else 0.1)),
                    float(value), decimals=decimals, default=float(p.default),
                )
                w.valueChanged.connect(lambda *_: self.changed.emit())
            w.setToolTip(p.tooltip)
            label = form_label(p.label)
            label.setToolTip(p.tooltip)
            self._widgets[p.key] = w
            layout.addRow(label, w)

    def values(self) -> dict[str, Any]:
        out: dict[str, Any] = {}
        for p in self._params:
            w = self._widgets[p.key]
            if isinstance(w, QComboBox):
                out[p.key] = w.currentData()
            elif isinstance(w, QCheckBox):
                out[p.key] = w.isChecked()
            elif p.kind == "int":
                out[p.key] = int(round(w.value()))
            else:
                out[p.key] = w.value()
        return out

    def reset(self) -> None:
        for p in self._params:
            w = self._widgets[p.key]
            if isinstance(w, QComboBox):
                set_combo(w, str(p.default))
            elif isinstance(w, QCheckBox):
                w.setChecked(bool(p.default))
            else:
                w.set_value(float(p.default))
        self.changed.emit()

    def install_filter(self, filt: QObject) -> None:
        for w in self.findChildren(QWidget):
            w.installEventFilter(filt)


#: Nomi italiani dei tasti, come sulle tastiere italiane.
_KEY_NAMES = (("Shift", "Maiusc"), ("Return", "Invio"), ("PgUp", "Pag↑"), ("PgDown", "Pag↓"))


def shortcut_text(action: QAction) -> str:
    text = action.shortcut().toString(QKeySequence.SequenceFormat.NativeText)
    for english, italian in _KEY_NAMES:
        text = text.replace(english, italian)
    return text


def kbd(text: str) -> QLabel:
    """Etichetta a forma di tasto: rende visibili le scorciatoie."""
    label = QLabel(text)
    label.setObjectName("kbd")
    label.setAlignment(Qt.AlignmentFlag.AlignCenter)
    return label


class ActionButton(QToolButton):
    """Pulsante della barra con icona, testo e scorciatoia sempre visibile (es. «Apri file…  [Ctrl+O]»)."""

    def __init__(self, action: QAction, text: str | None = None, show_text: bool = True):
        super().__init__()
        self._action = action
        self.setToolTip(action.toolTip())
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        row = QHBoxLayout(self)
        row.setContentsMargins(8, 3, 8, 3)
        row.setSpacing(6)
        self._icon = QLabel()
        if not action.icon().isNull():
            row.addWidget(self._icon)
        self._text = QLabel(text or action.text())
        self._text.setVisible(show_text)
        row.addWidget(self._text)
        key = shortcut_text(action)
        if key:
            row.addWidget(kbd(key))
        for w in self.findChildren(QLabel):
            w.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.clicked.connect(action.trigger)
        action.changed.connect(self._sync)
        self._sync()

    def _sync(self) -> None:
        enabled = self._action.isEnabled()
        self.setEnabled(enabled)
        self.setToolTip(self._action.toolTip())
        if not self._action.icon().isNull():
            mode = QIcon.Mode.Normal if enabled else QIcon.Mode.Disabled
            self._icon.setPixmap(self._action.icon().pixmap(QSize(18, 18), self.devicePixelRatioF(), mode))
        # Il colore delle etichette dipende dallo stato del pulsante: va ricalcolato a ogni cambio.
        for w in [self, *self.findChildren(QLabel)]:
            if w is not self:
                w.setProperty("off", not enabled)
            w.style().unpolish(w)
            w.style().polish(w)

    def setObjectName(self, name: str) -> None:  # noqa: N802 (API Qt)
        super().setObjectName(name)
        self._sync()

    def sizeHint(self) -> QSize:  # noqa: N802 (API Qt)
        hint = self.layout().sizeHint()
        return QSize(hint.width(), max(hint.height(), 30))

    def minimumSizeHint(self) -> QSize:  # noqa: N802
        return self.sizeHint()


class KeyBar(QFrame):
    """Striscia con i comandi principali della vista e i loro tasti."""

    def __init__(self, items: list[tuple[str, str]]):
        super().__init__()
        self.setObjectName("keybar")
        # Non deve imporre una larghezza minima alla finestra: le voci che non entrano si nascondono.
        self.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        row = QHBoxLayout(self)
        row.setContentsMargins(10, 4, 10, 4)
        row.setSpacing(0)
        self._items: list[QWidget] = []
        for key, text in items:
            item = QWidget()
            h = QHBoxLayout(item)
            h.setContentsMargins(0, 0, 14, 0)
            h.setSpacing(5)
            h.addWidget(kbd(key))
            label = QLabel(text)
            label.setObjectName("keytext")
            h.addWidget(label)
            row.addWidget(item)
            self._items.append(item)
        row.addStretch(1)

    def resizeEvent(self, event) -> None:  # noqa: N802 (API Qt)
        super().resizeEvent(event)
        available = self.width() - 20
        used = 0
        for item in self._items:
            used += item.sizeHint().width()
            item.setVisible(used <= available)
