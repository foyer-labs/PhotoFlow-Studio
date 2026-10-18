"""Pannello delle impostazioni: tutti i parametri sempre visibili, ordinati per importanza e spiegati."""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QFileDialog,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSizePolicy,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from ..algorithms import ALGORITHMS, get_algorithm
from ..settings import DEMOSAIC_ALGORITHMS, RAW_STAGES, WB_MODES, Settings
from .curve_editor import CurveEditor
from .style import OK, WARN, icon
from .widgets import (
    ExplainFilter,
    FloatControl,
    ParamForm,
    form_label,
    guard_wheel,
    hint_label,
    make_combo,
    make_form,
    set_combo,
)

_EXPLAIN_DEFAULT = (
    "Passa il mouse su un controllo per leggerne qui la spiegazione. "
    "Doppio clic su un cursore: torna al valore consigliato."
)

_EMPTY_CONTEXT = {"has_image": False, "is_raw": False, "is_bayer": False, "wb_equivalents": {}}

#: Cursori dei toni: (campo di ToneSettings, etichetta, spiegazione).
_TONE_CONTROLS = (
    ("contrast", "Contrasto", "Più contrasto: ombre più scure e luci più chiare, il grigio medio resta fermo. "
                              "Meno contrasto: un aspetto più morbido."),
    ("brightness", "Luminosità", "Schiarisce o scurisce i mezzitoni, lasciando fermi il nero e il bianco."),
    ("highlights", "Alte luci", "Verso −: recupera il dettaglio nelle zone molto chiare (cielo, nuvole, finestre), "
                                "anche quelle quasi bruciate dei RAW. Verso +: le rende più luminose."),
    ("shadows", "Ombre", "Verso +: schiarisce le zone scure facendone emergere il dettaglio (anche il rumore). "
                         "Verso −: le rende più profonde."),
    ("saturation", "Saturazione", "Intensità dei colori: −100 = bianco e nero."),
)


def _hot_threshold(decision: float) -> float:
    """«Decisione» 1..10 (più alto = più deciso) -> soglia in multipli del rumore (12..3)."""
    return round(13.0 - decision, 1)


def _hot_decision(threshold: float) -> float:
    return min(10.0, max(1.0, 13.0 - threshold))


class SettingsPanel(QWidget):
    denoiseChanged = Signal()
    toneChanged = Signal()
    developChanged = Signal()
    presetChosen = Signal(str)
    savePresetRequested = Signal()
    resetRequested = Signal()
    downloadRequested = Signal(str)
    dngConverterChosen = Signal(str)
    dngConverterReset = Signal()
    useDngConverterToggled = Signal(bool)
    applyRequested = Signal()
    #: Contagocce del bilanciamento: True = scegli un punto neutro nella foto.
    wbPickToggled = Signal(bool)

    def __init__(self, settings: Settings):
        super().__init__()
        self._settings = Settings.from_dict(settings.to_dict())
        self._settings.output.overwrite = False  # mai ricordato tra una sessione e l'altra
        self._params_by_algorithm: dict[str, dict] = {self._settings.denoise.algorithm: dict(self._settings.denoise.params)}
        self._ctx = _EMPTY_CONTEXT.copy()

        self.explain = QLabel(_EXPLAIN_DEFAULT)
        self.explain.setObjectName("explain")
        self.explain.setWordWrap(True)
        self.explain.setMinimumHeight(64)
        self.explain.setAlignment(Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignLeft)
        self._explain_filter = ExplainFilter(self.explain.setText)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(10, 4, 12, 10)
        layout.addWidget(self._build_photo())
        layout.addWidget(self._build_start())
        layout.addWidget(self._build_develop())
        layout.addWidget(self._build_denoise())
        self.algo_box = QGroupBox()
        self.algo_box.setObjectName("sub")
        self.algo_layout = QVBoxLayout(self.algo_box)
        self.algo_layout.setContentsMargins(0, 4, 0, 0)
        self.neural_status = QLabel()
        self.neural_status.setTextFormat(Qt.TextFormat.RichText)
        self.neural_status.setWordWrap(True)
        self.download_button = QPushButton(icon("download"), "Scarica il modello")
        self.download_button.setObjectName("primary")
        self.download_button.clicked.connect(lambda: self.downloadRequested.emit(self.algorithm.currentData()))
        self.algo_layout.addWidget(self.neural_status)
        self.algo_layout.addWidget(self.download_button)
        self.algo_hint = hint_label("I valori predefiniti vanno bene quasi sempre.")
        self.algo_layout.addWidget(self.algo_hint)
        self.algo_form: ParamForm | None = None
        reset_params = QPushButton("Ripristina i valori consigliati del metodo")
        reset_params.setObjectName("link")
        reset_params.clicked.connect(lambda: self.algo_form.reset() if self.algo_form else None)
        self._reset_params_button = reset_params
        layout.addWidget(self.algo_box)
        layout.addWidget(self._build_raw())
        layout.addStretch(1)
        self._rebuild_algorithm_form()
        self._install_explain(self)
        self.set_image_context(None)

    # ================================================================== sezioni

    def _build_photo(self) -> QWidget:
        box = QGroupBox("FOTO")
        v = QVBoxLayout(box)
        v.setSpacing(2)
        self.photo_title = QLabel("Nessuna foto aperta")
        self.photo_title.setObjectName("title")
        # Un nome di file lungo va a capo (o viene tagliato) invece di allargare tutto il pannello.
        self.photo_title.setWordWrap(True)
        self.photo_title.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
        self.photo_details = hint_label("Apri una foto RAW, JPG, PNG o TIFF per iniziare.")
        self.photo_noise = QLabel()
        self.photo_noise.setWordWrap(True)
        self.photo_noise.setTextFormat(Qt.TextFormat.RichText)
        self.photo_notes = hint_label()
        for w in (self.photo_title, self.photo_details, self.photo_noise, self.photo_notes):
            v.addWidget(w)
        self.photo_noise.hide()
        self.photo_notes.hide()
        return box

    def _build_start(self) -> QWidget:
        box = QGroupBox("PUNTO DI PARTENZA")
        v = QVBoxLayout(box)
        self.preset = QComboBox()
        guard_wheel(self.preset)
        self.preset.setToolTip(
            "Combinazioni pronte di impostazioni. «Bilanciato» toglie esattamente il rumore misurato ed è "
            "la scelta giusta quasi sempre. Dopo averne scelto uno puoi ritoccare tutto."
        )
        self.preset.activated.connect(lambda _i: self.presetChosen.emit(self.preset.currentData() or ""))
        v.addWidget(self.preset)
        self.preset_desc = hint_label()
        v.addWidget(self.preset_desc)
        row = QHBoxLayout()
        save = QPushButton("Salva come preset…")
        save.setToolTip("Salva le impostazioni correnti con un nome, per riusarle su altre foto.")
        save.clicked.connect(self.savePresetRequested)
        reset = QPushButton(icon("reset"), "Valori consigliati")
        reset.setToolTip("Riporta tutte le regolazioni del rumore e dello sviluppo ai valori consigliati.")
        reset.clicked.connect(self.resetRequested)
        row.addWidget(save)
        row.addWidget(reset)
        v.addLayout(row)
        steps = hint_label(
            "1. Doppio clic su una zona uniforme e scura per vederla al 100%.  "
            "2. Regola «Grana» finché il rumore sparisce senza effetto plastica.  "
            "3. Regola «Macchie di colore». Tutto il resto ha già valori consigliati."
        )
        v.addWidget(steps)
        return box

    def _build_denoise(self) -> QGroupBox:
        d = self._settings.denoise
        box = QGroupBox("RIDUZIONE DEL RUMORE")
        form = make_form(box)

        self.luma = FloatControl(0.0, 3.0, 0.05, d.luma_strength, default=1.0, center=1.0,
                                 hint="1 = rumore misurato · meno = più grana · più = più liscio")
        self.luma.setToolTip(
            "Quanta grana grigia togliere. 1 = esattamente il rumore misurato in questa foto: è il punto di "
            "partenza consigliato. 0,5 ne toglie metà e lascia una grana naturale. 1,5 rende più liscio, ma "
            "capelli e tessuti possono diventare «plastica». Giudica sempre al 100%."
        )
        self.luma.valueChanged.connect(self._on_denoise_value)
        self.luma_label = form_label("Grana")
        form.addRow(self.luma_label, self.luma)

        self.chroma = FloatControl(0.0, 4.0, 0.05, d.chroma_strength, default=1.0, center=1.0,
                                   hint="Puntini rossi, verdi e blu nelle zone scure")
        self.chroma.setToolTip(
            "Quanto togliere le macchioline colorate tipiche delle zone scure. 1 = rumore misurato. Puoi "
            "salire a 1,5–2 senza perdere nitidezza: al massimo i colori dei dettagli piccolissimi si spengono un po'."
        )
        self.chroma.valueChanged.connect(self._on_denoise_value)
        self.chroma_label = form_label("Macchie di colore")
        form.addRow(self.chroma_label, self.chroma)

        self.detail = FloatControl(0.0, 1.0, 0.05, d.detail, default=0.0,
                                   hint="Riporta un po' di grana fine e nitidezza")
        self.detail.setToolTip(
            "Rimette sopra il risultato una parte della foto originale, per ridare grana fine ed evitare "
            "l'effetto cera. 0 = niente. 0,1–0,2 = leggero (utile se la foto sembra di plastica). 1 = originale."
        )
        self.detail.valueChanged.connect(self._on_denoise_value)
        form.addRow(form_label("Recupero dettaglio"), self.detail)

        hot_tip = (
            "Toglie i puntini isolati troppo luminosi (pixel «bruciati» del sensore, frequenti nelle pose lunghe "
            "e con il sensore caldo). «Decisione» più alta ne toglie di più, ma oltre 8 rischi di cancellare "
            "anche stelle o piccoli riflessi veri."
        )
        self.hot_enabled = QCheckBox("Rimuovi i pixel caldi")
        self.hot_enabled.setChecked(d.hot_pixel_threshold > 0)
        self.hot_enabled.setToolTip(hot_tip)
        self.hot_enabled.toggled.connect(self._on_denoise_value)
        form.addRow(form_label("Pixel caldi"), self.hot_enabled)
        self.hot = FloatControl(1.0, 10.0, 0.5, _hot_decision(d.hot_pixel_threshold or 8.0), decimals=1, default=5.0)
        self.hot.setToolTip(hot_tip)
        self.hot.valueChanged.connect(self._on_denoise_value)
        form.addRow(form_label("Decisione"), self.hot)

        labels = {k: f"{a.label} — {a.summary}" for k, a in ALGORITHMS.items()}
        self.algorithm = make_combo(labels, d.algorithm, tooltips={k: a.description for k, a in ALGORITHMS.items()})
        self.algorithm.setToolTip(
            "Il metodo con cui il rumore viene separato dal dettaglio. Wavelet è veloce e va bene quasi sempre; "
            "i metodi IA danno in genere la qualità più alta ma sono lenti senza scheda video."
        )
        self.algorithm.currentIndexChanged.connect(self._on_algorithm)
        form.addRow(form_label("Metodo"), self.algorithm)

        self.raw_stage = make_combo(RAW_STAGES, d.raw_stage)
        self.raw_stage.setToolTip(
            "Vale solo per i file RAW. «Prima dello sviluppo» toglie il rumore dai dati grezzi del sensore, dove "
            "si distingue meglio dai dettagli veri: di solito dà il risultato più pulito. «Dopo lo sviluppo» lavora "
            "sulla foto già a colori: provalo se vedi artefatti."
        )
        self.raw_stage.currentIndexChanged.connect(self._on_denoise_value)
        form.addRow(form_label("Quando ridurre (RAW)"), self.raw_stage)
        self.stage_hint = hint_label()
        form.addRow("", self.stage_hint)

        self.apply_button = QPushButton("Applica la riduzione del rumore")
        self.apply_button.setObjectName("primary")
        self.apply_button.setMinimumHeight(32)
        self.apply_button.setToolTip(
            "Calcola il risultato con il metodo e le regolazioni scelte. Da quel momento l'anteprima si aggiorna "
            "da sola a ogni modifica; aprendo un'altra foto si riparte dall'originale."
        )
        self.apply_button.clicked.connect(self.applyRequested)
        self.apply_hint = hint_label()
        form.addRow(self.apply_button)
        form.addRow(self.apply_hint)
        self.set_applied(None)
        return box

    def _build_develop(self) -> QGroupBox:
        dv, tn = self._settings.develop, self._settings.tone
        box = QGroupBox("SVILUPPO")
        form = make_form(box)
        self.develop_hint = hint_label()
        form.addRow(self.develop_hint)

        self.curve = CurveEditor()
        self.curve.set_points(tn.curve)
        self.curve.curveChanged.connect(lambda _p: self.toneChanged.emit())
        self.curve.curveEdited.connect(lambda _p: self.toneChanged.emit())
        form.addRow(self.curve)
        curve_reset = QPushButton("Curva lineare")
        curve_reset.setObjectName("link")
        curve_reset.setToolTip("Toglie tutti i punti della curva dei toni.")
        curve_reset.clicked.connect(self.curve.reset)
        develop_reset = QPushButton("Ripristina lo sviluppo")
        develop_reset.setObjectName("link")
        develop_reset.setToolTip("Riporta bilanciamento, esposizione, toni, curva e saturazione ai valori di partenza.")
        develop_reset.clicked.connect(self._reset_develop)
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.addWidget(curve_reset)
        row.addStretch(1)
        row.addWidget(develop_reset)
        form.addRow(row)

        self.wb = make_combo(WB_MODES, dv.wb_mode)
        self.wb.setToolTip(
            "Corregge le dominanti di colore. «Come scattato» usa la scelta della fotocamera. «Automatico» la "
            "ricalcola dalla foto. «Luce diurna» è tarato sul sole di mezzogiorno. Muovendo Temperatura o Tinta "
            "si passa a «Personalizzato»."
        )
        self.wb.currentIndexChanged.connect(self._on_wb)
        self.pick_wb = QToolButton()
        self.pick_wb.setText("Contagocce")
        self.pick_wb.setCheckable(True)
        self.pick_wb.setToolTip("Fai clic su qualcosa che dovrebbe essere grigio o bianco nella foto (un muro, un "
                                "foglio, una nuvola): il bilanciamento lo rende neutro. Esc per annullare.")
        self.pick_wb.toggled.connect(self.wbPickToggled)
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.addWidget(self.wb, 1)
        row.addWidget(self.pick_wb)
        wb_row = QWidget()
        wb_row.setLayout(row)
        form.addRow(form_label("Bilanciamento bianco"), wb_row)

        self.temperature = FloatControl(2000.0, 15000.0, 50.0, dv.temperature, decimals=0, default=5500.0,
                                        center=5500.0, suffix=" K")
        self.temperature.setToolTip(
            "La luce della scena in kelvin: valori più alti scaldano la foto (più gialla), più bassi la raffreddano "
            "(più blu). Lampadine 2700–3200 K, sole 5000–5500 K, ombra e cielo coperto 6500–8000 K."
        )
        self.temperature.valueChanged.connect(self._on_wb_values)
        form.addRow(form_label("Temperatura"), self.temperature)
        self.tint = FloatControl(-150.0, 150.0, 1.0, dv.tint, decimals=0, default=0.0, center=0.0)
        self.tint.setToolTip("Corregge le dominanti verdi (verso −) o magenta (verso +), ad esempio sotto luci al "
                             "neon o tra le foglie.")
        self.tint.valueChanged.connect(self._on_wb_values)
        form.addRow(form_label("Tinta"), self.tint)

        self.exposure = FloatControl(-5.0, 5.0, 0.05, tn.exposure_ev, default=0.0, center=0.0, suffix=" EV")
        self.exposure.setToolTip(
            "Schiarisce (+) o scurisce (−) in stop: +1 = doppio della luce, −1 = metà. Schiarire rende anche il "
            "rumore più visibile."
        )
        self.exposure.valueChanged.connect(self.toneChanged)
        form.addRow(form_label("Esposizione"), self.exposure)
        self.auto_bright = QCheckBox("Luminosità automatica (RAW)")
        self.auto_bright.setChecked(tn.auto_brightness)
        self.auto_bright.setToolTip("Schiarisce il RAW come farebbe la fotocamera (le alte luci più forti restano intatte).")
        self.auto_bright.toggled.connect(self.toneChanged)
        form.addRow(form_label(""), self.auto_bright)

        self.tone_controls: dict[str, FloatControl] = {}
        for key, label, tip in _TONE_CONTROLS:
            control = FloatControl(-100.0, 100.0, 1.0, getattr(tn, key), decimals=0, default=0.0, center=0.0)
            control.setToolTip(tip)
            control.valueChanged.connect(self.toneChanged)
            form.addRow(form_label(label), control)
            self.tone_controls[key] = control
        return box

    def _build_raw(self) -> QGroupBox:
        dv = self._settings.develop
        box = QGroupBox("RAW")
        self.raw_box = box
        form = make_form(box)
        self.raw_hint = hint_label()
        form.addRow(self.raw_hint)

        self.demosaic = make_combo(DEMOSAIC_ALGORITHMS, dv.demosaic)
        self.demosaic.setToolTip(
            "Il sensore registra un solo colore per pixel: questo passaggio ricostruisce gli altri due. Le "
            "differenze si vedono solo al 100% sui dettagli finissimi: AHD va bene quasi sempre."
        )
        self.demosaic.currentIndexChanged.connect(self.developChanged)
        form.addRow(form_label("Ricostruzione colori"), self.demosaic)

        dng_tip = (
            "Programma gratuito di Adobe: serve solo per i RAW che questa app non sa ancora leggere (fotocamere "
            "appena uscite o NEF «alta efficienza»). Se è installato, il file viene convertito in DNG e aperto."
        )
        self.use_dng = QCheckBox("Usalo se un RAW non si apre")
        self.use_dng.setChecked(True)
        self.use_dng.setToolTip(dng_tip)
        self.use_dng.toggled.connect(self.useDngConverterToggled)
        form.addRow(form_label("DNG Converter"), self.use_dng)
        self.dng_label = hint_label()
        self.dng_label.setToolTip(dng_tip)
        choose = QPushButton("Scegli…")
        choose.setToolTip("Indica dove si trova «Adobe DNG Converter.exe», se non viene trovato da solo.")
        choose.clicked.connect(self._choose_dng_converter)
        clear = QPushButton("Ripristina")
        clear.setToolTip("Dimentica il percorso scelto e torna alla ricerca automatica.")
        clear.clicked.connect(self.dngConverterReset)
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.addWidget(self.dng_label, 1)
        row.addWidget(choose)
        row.addWidget(clear)
        dng_row = QWidget()
        dng_row.setLayout(row)
        form.addRow(form_label(""), dng_row)
        return box

    # ================================================================== eventi

    def _install_explain(self, root: QWidget) -> None:
        for w in root.findChildren(QWidget):
            w.installEventFilter(self._explain_filter)

    def _on_denoise_value(self, *_args) -> None:
        self._update_applicability()
        self.denoiseChanged.emit()

    def _rebuild_algorithm_form(self) -> None:
        if self.algo_form is not None:
            self.algo_layout.removeWidget(self.algo_form)
            self.algo_form.hide()  # sparisce subito, anche prima che Qt lo elimini davvero
            self.algo_form.deleteLater()
        algorithm = get_algorithm(self.algorithm.currentData())
        values = self._params_by_algorithm.get(algorithm.key, {})
        self.algo_form = ParamForm(algorithm.params, algorithm.resolve(values))
        self.algo_form.changed.connect(self._on_params)
        self.algo_layout.removeWidget(self._reset_params_button)
        self.algo_layout.addWidget(self.algo_form)
        self.algo_layout.addWidget(self._reset_params_button)
        self.algo_form.install_filter(self._explain_filter)
        self.algo_box.setTitle(f"Regolazioni avanzate · {algorithm.label}")
        self.algo_box.setToolTip(algorithm.description)
        self.refresh_algorithms()

    def _on_params(self) -> None:
        self._params_by_algorithm[self.algorithm.currentData()] = self.algo_form.values() if self.algo_form else {}
        self.denoiseChanged.emit()

    def _on_algorithm(self) -> None:
        self._rebuild_algorithm_form()
        self._update_applicability()
        self.denoiseChanged.emit()

    def _on_wb(self) -> None:
        self._show_wb_equivalent()
        self._update_applicability()
        self.toneChanged.emit()

    def _on_wb_values(self) -> None:
        """Temperatura o tinta mosse a mano: si passa a «Personalizzato»."""
        if self.wb.currentData() != "temperature":
            set_combo(self.wb, "temperature")
        self.toneChanged.emit()

    def _show_wb_equivalent(self) -> None:
        """Con «Come scattato», «Automatico» e «Luce diurna» i cursori mostrano la temperatura equivalente."""
        equivalent = self._ctx["wb_equivalents"].get(self.wb.currentData())
        if equivalent is None:
            return
        for control, value in zip((self.temperature, self.tint), equivalent):
            control.set_value(value)

    def set_wb(self, temperature: float, tint: float) -> None:
        """Bilanciamento scelto con il contagocce."""
        set_combo(self.wb, "temperature")
        self.temperature.set_value(temperature)
        self.tint.set_value(tint)
        self.pick_wb.setChecked(False)
        self._update_applicability()
        self.toneChanged.emit()

    def _reset_develop(self) -> None:
        fresh = Settings()
        current = self.settings()
        current.develop.wb_mode, current.develop.temperature, current.develop.tint = (
            fresh.develop.wb_mode, fresh.develop.temperature, fresh.develop.tint)
        current.tone = fresh.tone
        self.set_settings(current)
        self.toneChanged.emit()

    def _choose_dng_converter(self) -> None:
        path, _ = QFileDialog.getOpenFileName(self, "Adobe DNG Converter", "", "Programmi (*.exe);;Tutti i file (*)")
        if path:
            self.dngConverterChosen.emit(path)

    # ================================================================== applicabilità

    def _update_applicability(self) -> None:
        ctx = self._ctx
        is_raw, is_bayer = ctx["is_raw"], ctx["is_bayer"]
        algorithm = get_algorithm(self.algorithm.currentData())

        # Reti «cieche»: la forza è una miscela con l'originale, oltre 1 non ha effetto.
        blind = algorithm.blind
        self.luma.set_maximum(1.0 if blind else 3.0)
        self.chroma.set_maximum(1.0 if blind else 4.0)
        self.luma_label.setText("Grana (quanto applicare)" if blind else "Grana")
        self.chroma_label.setText("Colore (quanto applicare)" if blind else "Macchie di colore")
        self.luma.set_hint("1 = risultato completo · 0 = foto originale" if blind else
                           "1 = rumore misurato · meno = più grana · più = più liscio")
        self.detail.setEnabled(self.luma.value() > 0)
        self.hot.setEnabled(self.hot_enabled.isChecked())

        stage_ok = is_raw and is_bayer
        self.raw_stage.setEnabled(stage_ok)
        if not ctx["has_image"]:
            self.stage_hint.setText("")
        elif not is_raw:
            self.stage_hint.setText("Non applicabile: la foto non è un RAW.")
        elif not is_bayer:
            self.stage_hint.setText("Questo sensore (es. Fujifilm X-Trans) usa sempre «Dopo lo sviluppo».")
        elif self.raw_stage.currentData() == "rgb":
            self.stage_hint.setText("Attenzione: dopo lo sviluppo il risultato è in genere peggiore, soprattutto nelle "
                                    "ombre profonde. Usalo solo per confronto.")
        else:
            self.stage_hint.setText("")
        self.stage_hint.setVisible(bool(self.stage_hint.text()))

        # Sviluppo
        has_image = ctx["has_image"]
        for w in (self.curve, self.wb, self.pick_wb, self.temperature, self.tint, self.exposure,
                  *self.tone_controls.values()):
            w.setEnabled(has_image)
        self.auto_bright.setEnabled(is_raw)
        model = self.wb.model()
        for key in ("daylight", "custom"):
            item = model.item(self.wb.findData(key))
            if item is not None:  # luce diurna e moltiplicatori hanno senso solo per i RAW
                item.setEnabled(is_raw or not has_image)
        if not has_image:
            develop = "Apri una foto per regolare colori e toni."
        elif not is_raw:
            develop = "Su JPG, PNG e TIFF le alte luci bruciate dalla fotocamera non si recuperano."
        else:
            develop = ""
        self.develop_hint.setText(develop)
        self.develop_hint.setVisible(bool(develop))
        self.demosaic.setEnabled(is_raw)
        reason = ""
        if not has_image:
            reason = "Apri una foto RAW per usare queste impostazioni."
        elif not is_raw:
            reason = "Non applicabile: la foto non è un RAW."
        self.raw_hint.setText(reason)
        self.raw_hint.setVisible(bool(reason))

    # ================================================================== stato dall'esterno

    def set_image_context(self, info: dict | None) -> None:
        """Aggiorna il riquadro «Foto» e l'applicabilità dei controlli.

        ``info``: title, details, noise (HTML), notes, is_raw, is_bayer e ``wb_equivalents``: temperatura e
        tinta equivalenti a «Come scattato», «Automatico» e «Luce diurna» ({modo: (K, tinta)}).
        """
        if info is None:
            self._ctx = _EMPTY_CONTEXT.copy()
            self.photo_title.setText("Nessuna foto aperta")
            self.photo_details.setText("Apri una foto RAW, JPG, PNG o TIFF per iniziare.")
            self.photo_noise.hide()
            self.photo_notes.hide()
        else:
            self._ctx = {"has_image": True, "is_raw": info["is_raw"], "is_bayer": info["is_bayer"],
                         "wb_equivalents": info.get("wb_equivalents") or {}}
            self._show_wb_equivalent()
            self.photo_title.setText(info["title"])
            self.photo_title.setToolTip(info["title"])
            self.photo_details.setText(info["details"])
            self.photo_noise.setText(info.get("noise", ""))
            self.photo_noise.setVisible(bool(info.get("noise")))
            self.photo_notes.setText(info.get("notes", ""))
            self.photo_notes.setVisible(bool(info.get("notes")))
        self._update_applicability()

    def set_presets(self, entries: list[tuple[str, str]]) -> None:
        """``entries``: (nome, descrizione)."""
        self.preset.blockSignals(True)
        self.preset.clear()
        self.preset.addItem("Personalizzato", "")
        for name, desc in entries:
            self.preset.addItem(name, name)
            self.preset.setItemData(self.preset.count() - 1, desc, Qt.ItemDataRole.ToolTipRole)
        self.preset.blockSignals(False)

    def show_preset(self, name: str, description: str, modified: bool) -> None:
        index = self.preset.findData(name) if name else 0
        self.preset.blockSignals(True)
        self.preset.setCurrentIndex(max(0, index))
        if name and index >= 0:
            self.preset.setItemText(index, f"{name} (modificato)" if modified else name)
        for i in range(1, self.preset.count()):
            if i != index:
                self.preset.setItemText(i, self.preset.itemData(i))
        self.preset.blockSignals(False)
        self.preset_desc.setText(description)
        self.preset_desc.setVisible(bool(description))

    def set_dng_converter(self, path: str | None, enabled: bool) -> None:
        self.use_dng.blockSignals(True)
        self.use_dng.setChecked(enabled)
        self.use_dng.blockSignals(False)
        self.dng_label.setText(path if path else "Non installato: serve solo se un RAW non si apre.")

    def set_applied(self, applied: bool | None, shortcut: str = "") -> None:
        """Stato del pulsante «Applica»: None = nessuna foto, False = da applicare, True = applicata."""
        key = f"   ({shortcut})" if shortcut else ""
        if applied:
            self.apply_button.setText("✓ Applicata: l'anteprima segue le regolazioni")
            self.apply_button.setObjectName("")
            self.apply_hint.setText("")
        else:
            self.apply_button.setText(f"Applica la riduzione del rumore{key}")
            self.apply_button.setObjectName("primary" if applied is False else "")
            self.apply_hint.setText("Scegli il metodo e le regolazioni, poi applica: finché non lo fai vedi la "
                                    "foto originale." if applied is False else "")
        self.apply_button.setEnabled(applied is False)
        self.apply_hint.setVisible(bool(self.apply_hint.text()))
        self.apply_button.style().unpolish(self.apply_button)
        self.apply_button.style().polish(self.apply_button)

    def set_algorithm(self, key: str) -> None:
        """Sceglie il metodo (es. dalla finestra di esportazione), con le sue regolazioni avanzate."""
        index = self.algorithm.findData(key)
        if index >= 0 and index != self.algorithm.currentIndex():
            self.algorithm.setCurrentIndex(index)  # emette denoiseChanged

    def set_output(self, output) -> None:
        """Salvataggio scelto nella finestra di esportazione: si ricorda per la prossima volta."""
        self._settings.output = Settings.from_dict({"output": output.__dict__}).output

    def current_algorithm(self):
        return get_algorithm(self.algorithm.currentData())

    def refresh_algorithms(self, download_text: str | None = None) -> None:
        """Aggiorna lo stato dei modelli neurali (dopo un download, o durante)."""
        for i in range(self.algorithm.count()):
            alg = get_algorithm(self.algorithm.itemData(i))
            text = f"{alg.label} — {alg.summary}"
            if not alg.is_available():
                text += f" (da scaricare, {alg.spec.size_mb} MB)"
            self.algorithm.setItemText(i, text)
        algorithm = self.current_algorithm()
        if not algorithm.joint:
            self.neural_status.hide()
            self.download_button.hide()
            self.algo_hint.setText("I valori predefiniti vanno bene quasi sempre.")
            return
        from ..neural.runtime import gpu_status

        spec = algorithm.spec
        has_gpu, device_text = gpu_status()
        if algorithm.is_available():
            model_chip = f"<span style='color:{OK}'>● Modello pronto</span>"
        else:
            model_chip = f"<span style='color:{WARN}'>↓ Da scaricare ({spec.size_mb} MB, una sola volta)</span>"
        device_chip = (f"<span style='color:{OK}'>● {device_text}</span>" if has_gpu
                       else f"<span style='color:{WARN}'>▲ {device_text}</span>")
        self.neural_status.setText(f"{model_chip} &nbsp; {device_chip}")
        self.neural_status.setToolTip(f"{algorithm.description}\n\nLicenza del modello: {spec.license}.")
        self.neural_status.show()
        if download_text is not None:
            self.download_button.setText(download_text)
            self.download_button.setEnabled(False)
            self.download_button.show()
        elif algorithm.is_available():
            self.download_button.hide()
        else:
            self.download_button.setText(f"Scarica {spec.label} ({spec.size_mb} MB)")
            self.download_button.setEnabled(True)
            self.download_button.show()
        self.algo_hint.setText(algorithm.description)

    # ================================================================== lettura e scrittura

    def settings(self) -> Settings:
        s = self._settings
        d = s.denoise
        d.algorithm = self.algorithm.currentData()
        d.raw_stage = self.raw_stage.currentData()
        d.params = self.algo_form.values() if self.algo_form else {}
        d.luma_strength = self.luma.value()
        d.chroma_strength = self.chroma.value()
        d.detail = self.detail.value()
        d.hot_pixel_threshold = _hot_threshold(self.hot.value()) if self.hot_enabled.isChecked() else 0.0
        s.develop.wb_mode = self.wb.currentData()
        s.develop.temperature = self.temperature.value()
        s.develop.tint = self.tint.value()
        s.develop.demosaic = self.demosaic.currentData()
        s.tone.auto_brightness = self.auto_bright.isChecked()
        s.tone.exposure_ev = self.exposure.value()
        for key, control in self.tone_controls.items():
            setattr(s.tone, key, control.value())
        s.tone.curve = self.curve.points()
        # Il salvataggio non ha controlli nel pannello: si sceglie nella finestra «Esporta».
        return Settings.from_dict(s.to_dict())

    def set_settings(self, settings: Settings) -> None:
        """Carica le impostazioni (preset, annulla/ripeti) senza emettere un segnale per ogni controllo."""
        self._settings = Settings.from_dict(settings.to_dict())
        s = self._settings
        self._params_by_algorithm[s.denoise.algorithm] = dict(s.denoise.params)
        widgets = [self.algorithm, self.raw_stage, self.wb, self.demosaic, self.auto_bright, self.hot_enabled]
        for w in widgets:
            w.blockSignals(True)
        try:
            set_combo(self.algorithm, s.denoise.algorithm)
            set_combo(self.raw_stage, s.denoise.raw_stage)
            self.luma.set_value(s.denoise.luma_strength)
            self.chroma.set_value(s.denoise.chroma_strength)
            self.detail.set_value(s.denoise.detail)
            self.hot_enabled.setChecked(s.denoise.hot_pixel_threshold > 0)
            if s.denoise.hot_pixel_threshold > 0:
                self.hot.set_value(_hot_decision(s.denoise.hot_pixel_threshold))
            set_combo(self.wb, s.develop.wb_mode)
            self.temperature.set_value(s.develop.temperature)
            self.tint.set_value(s.develop.tint)
            set_combo(self.demosaic, s.develop.demosaic)
            self.auto_bright.setChecked(s.tone.auto_brightness)
            self.exposure.set_value(s.tone.exposure_ev)
            for key, control in self.tone_controls.items():
                control.set_value(getattr(s.tone, key))
            self.curve.set_points(s.tone.curve)
        finally:  # i segnali non devono restare bloccati anche se un valore non è valido
            for w in widgets:
                w.blockSignals(False)
        self._rebuild_algorithm_form()
        self._update_applicability()
        if s.develop.wb_mode != "temperature":
            self._show_wb_equivalent()
