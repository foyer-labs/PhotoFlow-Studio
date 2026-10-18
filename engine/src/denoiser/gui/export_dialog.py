"""Finestra «Esporta»: tutte le scelte del salvataggio, per una foto o per quelle selezionate.

È l'unico posto in cui si sceglie come salvare: il pannello a destra regola solo la foto.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtGui import QAction
from PySide6.QtWidgets import (
    QButtonGroup,
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMenu,
    QRadioButton,
    QSpinBox,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from ..algorithms import ALGORITHMS, get_algorithm
from ..batch import EXISTING_POLICIES
from ..image import ImageMeta
from ..io.metadata import find_exiftool
from ..io.naming import PLACEHOLDERS, output_path
from ..settings import OUTPUT_FORMATS, OutputSettings
from .style import WARN, icon
from .widgets import form_label, hint_label, make_combo, make_form, set_combo

_NON_RAW_FORMATS = {k: v for k, v in OUTPUT_FORMATS.items() if k != "dng"}
_DEFAULT_SUBFOLDER = "denoised"
_FORMAT_TIPS = {
    "tiff": "TIFF: massima qualità, per continuare il ritocco in altri programmi.",
    "jpeg": "JPEG: file piccolo, per condividere e stampare.",
    "png": "PNG: senza perdita di qualità, file grandi.",
    "dng": "DNG: un RAW ripulito, da sviluppare ancora in Lightroom, darktable e simili (solo da foto RAW).",
}


@dataclass
class ExportChoice:
    """Scelte fatte nella finestra. ``folder`` ha lo stesso significato di :attr:`OutputSettings.folder`."""

    folder: str
    format: str
    bit_depth: int
    jpeg_quality: int
    name_template: str = "{nome}"
    copy_metadata: bool = True
    #: Formato per JPG, PNG e TIFF quando è stato scelto il DNG (che si crea solo dai RAW).
    non_raw_format: str = "tiff"
    #: Solo per più foto: cosa fare se il file esiste già (vedi :data:`EXISTING_POLICIES`).
    policy: str = "rename"
    #: Solo per più foto: metodo di riduzione del rumore imposto a tutte (None = quello di ogni foto).
    algorithm: str | None = None

    def output(self, base: OutputSettings) -> OutputSettings:
        """``base`` con le scelte fatte qui."""
        return replace(base, folder=self.folder, format=self.format, bit_depth=self.bit_depth,
                       jpeg_quality=self.jpeg_quality, name_template=self.name_template,
                       copy_metadata=self.copy_metadata, overwrite=False)


class ExportDialog(QDialog):
    """``raw_count``/``non_raw_count``: quante foto RAW e non RAW verranno esportate. ``algorithm``:
    se indicato (anche "") la finestra fa scegliere il metodo (esportazione di più foto)."""

    def __init__(self, parent: QWidget | None, output: OutputSettings, example: ImageMeta, raw_count: int,
                 non_raw_count: int, algorithm: str | None = None, non_raw_format: str = "tiff",
                 policy: str = "rename", start_dir: str = ""):
        super().__init__(parent)
        many = raw_count + non_raw_count > 1
        self.setWindowTitle(f"Esporta {raw_count + non_raw_count} foto" if many else f"Esporta {example.source.name}")
        self.setMinimumWidth(600)
        self._output = replace(output)
        self._example = example
        self._many = many
        self._raw_count, self._non_raw_count = raw_count, non_raw_count
        self._start_dir = start_dir
        v = QVBoxLayout(self)

        # ---------------------------------------------------------------- dove
        v.addWidget(QLabel("<b>Dove salvare</b>"))
        self.same = QRadioButton("Nella stessa cartella della foto originale")
        self.sub = QRadioButton("In una sottocartella accanto alla foto originale:")
        self.chosen = QRadioButton("In questa cartella:")
        group = QButtonGroup(self)
        for b in (self.same, self.sub, self.chosen):
            group.addButton(b)
        self.sub_name = QLineEdit()
        self.sub_name.setPlaceholderText(_DEFAULT_SUBFOLDER)
        self.sub_name.setToolTip("Nome della sottocartella, creata accanto a ogni foto se non esiste")
        self.sub_name.textEdited.connect(lambda _t: self.sub.setChecked(True))
        self.folder = QLineEdit()
        self.folder.setPlaceholderText("Scegli una cartella…")
        self.folder.textEdited.connect(lambda _t: self.chosen.setChecked(True))
        browse = QToolButton()
        browse.setIcon(icon("folder"))
        browse.setText("Sfoglia…")
        browse.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
        browse.clicked.connect(self._browse)
        v.addWidget(self.same)
        row = QHBoxLayout()
        row.addWidget(self.sub)
        row.addWidget(self.sub_name, 1)
        v.addLayout(row)
        row = QHBoxLayout()
        row.addWidget(self.chosen)
        row.addWidget(self.folder, 1)
        row.addWidget(browse)
        v.addLayout(row)
        folder = output.folder.strip()
        if not folder:
            self.same.setChecked(True)
            self.sub_name.setText(_DEFAULT_SUBFOLDER)
        elif Path(folder).is_absolute():
            self.chosen.setChecked(True)
            self.folder.setText(folder)
            self.sub_name.setText(_DEFAULT_SUBFOLDER)
        else:
            self.sub.setChecked(True)
            self.sub_name.setText(folder)

        # ---------------------------------------------------------------- come
        v.addSpacing(6)
        v.addWidget(QLabel("<b>Formato e nome</b>"))
        form = make_form()
        self.format = make_combo(OUTPUT_FORMATS, output.format, tooltips=_FORMAT_TIPS)
        self.format.setToolTip("\n".join(_FORMAT_TIPS.values()))
        dng = self.format.model().item(self.format.findData("dng"))
        if raw_count == 0:
            dng.setEnabled(False)
            dng.setToolTip("Il DNG si può creare solo da una foto RAW.")
            if output.format == "dng":
                set_combo(self.format, "tiff")
        self.format.currentIndexChanged.connect(self._update)
        form.addRow(form_label("Formato"), self.format)
        self.non_raw = make_combo(_NON_RAW_FORMATS, non_raw_format)
        self.non_raw.currentIndexChanged.connect(self._update)
        self.non_raw_label = form_label("Foto non RAW")
        form.addRow(self.non_raw_label, self.non_raw)
        self.non_raw_hint = hint_label(
            f"{non_raw_count} foto (JPG, PNG o TIFF) non possono diventare DNG, che si crea solo dai RAW: "
            "scegli in che formato salvarle."
        )
        form.addRow("", self.non_raw_hint)
        self.bits = make_combo({"16": "16 bit (per ritoccare ancora)", "8": "8 bit (stampa e web)"},
                               str(output.bit_depth))
        self.bits.setToolTip("16 bit conserva le sfumature se ritoccherai ancora la foto (file più grandi). 8 bit "
                             "basta per stampa e web. Vale per TIFF e PNG.")
        form.addRow(form_label("Bit per colore"), self.bits)
        self.quality = QSpinBox()
        self.quality.setRange(1, 100)
        self.quality.setValue(output.jpeg_quality)
        self.quality.setToolTip("Qualità JPEG: 90–95 = ottima. Sotto 80 compaiono blocchetti.")
        form.addRow(form_label("Qualità JPEG"), self.quality)
        self.dng_hint = hint_label(
            "Il DNG resta un RAW da sviluppare: bilanciamento ed esposizione valgono ovunque, toni e curva "
            "diventano istruzioni per Lightroom e Photoshop."
        )
        self.dng_hint.setToolTip("Ricostruzione dei colori e luminosità automatica le sceglie il programma di "
                                 "sviluppo; contrasto, alte luci, ombre, saturazione e curva arrivano come "
                                 "impostazioni di Camera Raw, che darktable e Capture One leggono solo in parte.")
        form.addRow("", self.dng_hint)

        self.name = QLineEdit(output.name_template or "{nome}")
        self.name.setToolTip("Nome del file, senza estensione. {nome} è il nome dell'originale. Segnaposto:\n"
                             + "\n".join(f"{{{k}}}: {d}" for k, d in PLACEHOLDERS.items()))
        self.name.textChanged.connect(self._update)
        insert = QToolButton()
        insert.setText("{…}")
        insert.setToolTip("Inserisci un segnaposto nel nome")
        insert.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        menu = QMenu(insert)
        for key, desc in PLACEHOLDERS.items():
            act = QAction(f"{{{key}}} — {desc}", menu)
            act.triggered.connect(lambda _c=False, k=key: self.name.insert("{" + k + "}"))
            menu.addAction(act)
        insert.setMenu(menu)
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.addWidget(self.name, 1)
        row.addWidget(insert)
        name_row = QWidget()
        name_row.setLayout(row)
        form.addRow(form_label("Nome del file"), name_row)

        self.copy_metadata = QCheckBox("Copia i metadati dell'originale")
        self.copy_metadata.setChecked(output.copy_metadata)
        self.copy_metadata.setToolTip(
            "Data, fotocamera, obiettivo, esposizione, GPS, parole chiave… copiati nel file salvato. "
            + ("Si usa ExifTool." if find_exiftool() else "ExifTool non trovato: vengono copiati solo i dati "
                                                         "essenziali dei JPG.")
        )
        form.addRow(form_label("Metadati"), self.copy_metadata)

        # ---------------------------------------------------------------- più foto
        self.algorithm: QComboBox | None = None
        self.policy: QComboBox | None = None
        if algorithm is not None:
            labels = {"": "Quello di ogni foto (consigliato)"}
            for key, alg in ALGORITHMS.items():
                text = f"{alg.label} — {alg.summary}"
                if not alg.is_available():
                    text += f" (da scaricare, {alg.spec.size_mb} MB)"
                labels[key] = text
            self.algorithm = make_combo(labels, algorithm, tooltips={k: a.description for k, a in ALGORITHMS.items()})
            self.algorithm.setToolTip("Ogni foto viene esportata con le sue regolazioni. Qui puoi usare per tutte lo "
                                      "stesso metodo di riduzione del rumore, con i suoi valori consigliati.")
            self.algorithm.currentIndexChanged.connect(self._update)
            form.addRow(form_label("Metodo"), self.algorithm)
            self.policy = make_combo(EXISTING_POLICIES, policy)
            form.addRow(form_label("Se il file esiste"), self.policy)
        v.addLayout(form)

        self.example_label = hint_label()
        self.example_label.setTextFormat(Qt.TextFormat.RichText)
        v.addWidget(self.example_label)
        self.buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Cancel)
        self.ok = self.buttons.addButton("Esporta", QDialogButtonBox.ButtonRole.AcceptRole)
        self.ok.setObjectName("primary")
        self.ok.setDefault(True)
        self.buttons.accepted.connect(self.accept)
        self.buttons.rejected.connect(self.reject)
        v.addWidget(self.buttons)
        # Collegati solo ora: prima della fine della costruzione non tutti i controlli esistono.
        for b in (self.same, self.sub, self.chosen):
            b.toggled.connect(self._update)
        for w in (self.sub_name, self.folder):
            w.textChanged.connect(self._update)
        self._update()

    # ------------------------------------------------------------------

    def _browse(self) -> None:
        start = self.folder.text().strip() or self._start_dir or str(self._example.source.parent)
        folder = QFileDialog.getExistingDirectory(self, "Cartella in cui salvare", start)
        if folder:
            self.folder.setText(str(Path(folder)))
            self.chosen.setChecked(True)

    def _folder_value(self) -> str:
        if self.same.isChecked():
            return ""
        if self.sub.isChecked():
            return self.sub_name.text().strip() or _DEFAULT_SUBFOLDER
        return self.folder.text().strip()

    def _update(self, *_args) -> None:
        fmt = self.format.currentData()
        pixels_format = self.non_raw.currentData() if fmt == "dng" else fmt
        mixed = fmt == "dng" and self._non_raw_count > 0
        shown = (mixed, fmt == "dng")
        for w in (self.non_raw_label, self.non_raw, self.non_raw_hint):
            w.setVisible(mixed)
        self.bits.setEnabled(pixels_format in ("tiff", "png") and (fmt != "dng" or mixed))
        self.quality.setEnabled(pixels_format == "jpeg" and (fmt != "dng" or mixed))
        self.dng_hint.setVisible(fmt == "dng")
        folder = self._folder_value()
        folder_ok = not self.chosen.isChecked() or bool(folder and Path(folder).is_absolute())
        problems = []
        if not folder_ok:
            problems.append("Scegli la cartella in cui salvare (Sfoglia…).")
        key = self.algorithm.currentData() if self.algorithm is not None else ""
        if key and not get_algorithm(key).is_available():
            spec = get_algorithm(key).spec
            problems.append(f"{spec.label} non è ancora sul computer: alla conferma ti verrà proposto di "
                            f"scaricarlo ({spec.size_mb} MB, una sola volta).")
        text = ""
        if folder_ok:
            out = replace(self._output, folder=folder, format=fmt, name_template=self.name.text().strip() or "{nome}")
            example = output_path(self._example, out, key or "wavelet")
            text = f"{'Ad esempio' if self._many else 'Verrà salvato come'}: {example}"
        if self.same.isChecked():
            text += ("<br>Se il formato è quello dell'originale, al nome si aggiunge «_1»: l'originale non viene "
                     "mai sovrascritto.")
        if problems:
            text += ("<br>" if text else "") + "<br>".join(f"<span style='color:{WARN}'>{p}</span>" for p in problems)
        self.example_label.setText(text)
        self.ok.setEnabled(folder_ok)
        if shown != getattr(self, "_shown_rows", None):
            # Righe comparse o sparite: la finestra si adatta (i testi a capo non restano tagliati).
            self._shown_rows = shown
            self.layout().activate()
            self.adjustSize()

    def choice(self) -> ExportChoice:
        return ExportChoice(
            folder=self._folder_value(),
            format=self.format.currentData(),
            bit_depth=int(self.bits.currentData()),
            jpeg_quality=self.quality.value(),
            name_template=self.name.text().strip() or "{nome}",
            copy_metadata=self.copy_metadata.isChecked(),
            non_raw_format=self.non_raw.currentData(),
            policy=self.policy.currentData() if self.policy is not None else "rename",
            algorithm=(self.algorithm.currentData() or None) if self.algorithm is not None else None,
        )


__all__ = ["ExportChoice", "ExportDialog"]
