# Sviluppo

[← Torna al README](../README.md)

## Ambiente

Serve Python 3.11 o più recente.

```bat
python -m venv .venv
.venv\Scripts\activate            :: su Linux/macOS: source .venv/bin/activate
pip install -e ".[gui,dev]"
pytest                            :: test
denoiser-gui                      :: interfaccia
denoiser analizza foto.nef        :: riga di comando
```

Extra opzionali di `pyproject.toml`: `gui` (PySide6), `dev` (pytest), `build` (PyInstaller).
Su Windows viene installato `onnxruntime-directml`, altrove `onnxruntime`.

## Struttura del codice

```
src/denoiser/
├── cli.py              riga di comando (elabora, analizza, algoritmi, modelli, preset)
├── settings.py         impostazioni (dataclass) con lettura tollerante dei valori
├── presets.py          preset predefiniti e personali
├── pipeline.py         analisi, anteprima di una zona, giudizio sul rumore
├── develop.py          sviluppo: bilanciamento (temperatura/tinta), toni, curva, saturazione, istogramma, XMP Camera Raw
├── sidecar.py          regolazioni di ogni foto nel file accanto (DSC_0001.NEF.denoiser)
├── bayer.py            livello 3: denoise dei quattro piani Bayer e sviluppo con LibRaw
├── raw_noise.py        modello del rumore del sensore e «gemello di rumore»
├── noise.py            stima del rumore (zone uniformi, inviluppo inferiore)
├── vst.py              trasformata di Anscombe generalizzata pesata per canale
├── joint.py            reti neurali: forze separate su luminanza e colore
├── hotpixel.py         pixel caldi sulle immagini già sviluppate
├── color.py            trasformazioni di colore (opponente, sRGB)
├── batch.py            esportazione atomica ed esportazione di più file (nomi, file esistenti, DNG solo dai RAW)
├── algorithms/         wavelet, nlm, bilateral, neural (interfaccia comune Denoiser/Param)
├── neural/             lettura sicura dei .pth, grafi ONNX di DRUNet, SCUNet e Restormer, tasselli, runtime, download
├── io/                 lettura (RAW, JPG/PNG/TIFF, DNG Converter), scrittura (TIFF/PNG/JPEG/DNG), metadati
└── gui/                interfaccia PySide6
    ├── main_window.py  finestra, menu, anteprima, esportazione, annulla/ripeti
    ├── split_view.py   vista prima/dopo (zoom in pixel fisici, piramide di riduzione)
    ├── splash.py       schermata di avvio con barra di avanzamento
    ├── filmstrip.py    carosello delle foto di una cartella (miniature, selezione, applica alle selezionate)
    ├── curve_editor.py curva dei toni modificabile sopra l'istogramma
    ├── export_dialog.py finestra «Esporta» (una foto o N): cartella, formato, nome, metadati, metodo
    ├── settings_panel.py, widgets.py, style.py, workers.py
packaging/              PyInstaller: denoiser.spec, icona, punti d'avvio
tests/                  test pytest con immagini e DNG sintetici
docs/                   documentazione e schermate
```

## Test

```bat
pytest -q
```

- I test usano immagini sintetiche, compresi **DNG Bayer generati con rumore noto**
  (`tests/synthetic.py`), così da misurare oggettivamente il miglioramento (PSNR) e controllare
  orientamento, colori, metadati e DNG di uscita.
- Le reti ricostruite in ONNX vengono confrontate con implementazioni di riferimento in numpy.
  Con `DENOISER_TEST_WEIGHTS=<cartella con i .pth>` (e `DENOISER_MODELS_DIR` sulla stessa cartella)
  si eseguono anche i test con i pesi reali.
- `tests/test_regressioni.py` contiene i casi emersi nelle revisioni del codice.
- Per i test dei metadati serve ExifTool nel `PATH` (su Linux: `apt install libimage-exiftool-perl`).

## Eseguibile per Windows

```bat
pip install -e ".[gui,build]"
pyinstaller packaging/denoiser.spec --noconfirm
```

Il risultato è `dist\Denoiser\` con `Denoiser.exe` (interfaccia) e `denoiser-cli.exe`. Per la copia
completa dei metadati va aggiunto ExifTool in `dist\Denoiser\exiftool\exiftool.exe`: la build
automatica lo scarica da exiftool.org.

## Build automatica

Il workflow [`.github/workflows/build.yml`](../.github/workflows/build.yml) parte a ogni push:

1. **test** su Windows e Linux;
2. **build-windows**: crea l'eseguibile, include ExifTool e lo collauda. Esegue la riga di comando
   su un DNG sintetico (wavelet → TIFF, NLM → DNG), scarica DRUNet ed elabora un'immagine, avvia
   l'interfaccia. Pubblica lo zip come artefatto *Denoiser-windows* (30 giorni);
3. **release**: solo per i tag `vX.Y.Z` o per un avvio manuale con la versione indicata (vedi sotto).

## Pubblicare una versione

1. Aggiorna la versione in `src/denoiser/__init__.py` e in `pyproject.toml`.
2. In `CHANGELOG.md` sposta le voci di *Non rilasciato* in una nuova sezione `## [X.Y.Z] - data`.
3. Fai il commit e pubblica la versione in uno dei due modi:
   - da GitHub: *Actions → Test e build Windows → Run workflow*, scegli il branch e scrivi la
     versione (es. `0.9.0`) nel campo *versione*. Il tag `v0.9.0` viene creato dal workflow;
   - da terminale, inviando il tag:

     ```bash
     git tag -a vX.Y.Z -m "Denoiser X.Y.Z"
     git push origin vX.Y.Z
     ```

Il job *release* controlla che il tag corrisponda alla versione del codice e scrive la descrizione
della release con [`.github/scripts/note_di_rilascio.py`](../.github/scripts/note_di_rilascio.py):
presentazione, schermata, novità dalla sezione del CHANGELOG, installazione, requisiti, verifica del
download e collegamenti alla documentazione. Poi crea la release su GitHub con `Denoiser-windows-X.Y.Z.zip` e la sua
impronta SHA-256. Solo le versioni con un trattino (es. `1.0.0-rc1`) vengono segnate come
*pre-release*.

Per correggere la descrizione di una release già pubblicata (es. dopo aver ritoccato il CHANGELOG o il
modello delle note) senza rifare la build, o per aggiornarne il segno di *pre-release*: *Actions → Aggiorna la descrizione di una release →
Run workflow*, indicando la versione.

## Schermate della documentazione

Le schermate in `docs/screenshots/` sono catturate dall'applicazione vera, senza schermo
(`QT_QPA_PLATFORM=offscreen`), a 1440×900. Le foto di prova sono immagini di esempio di
scikit-image con rumore sintetico, salvate come DNG con `tests/synthetic.py`.

## Convenzioni

- Codice, commenti, messaggi e documentazione in italiano; nomi di variabili e funzioni in inglese.
- Ogni testo dell'interfaccia rivolto all'utente evita il gergo tecnico e spiega cosa fare.
- I pesi dei modelli neurali non vanno mai aggiunti al repository.
