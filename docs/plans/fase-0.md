# Piano della fase 0 — Setup e laboratorio

Stato: **approvato** il 2026-10-12 (Avalonia 12.1.4, download del laboratorio autorizzati, push su GitHub autorizzato). Branch: `phase/0-setup`.

Riferimenti: spec §3, §5, §11, §12bis, §13 (fase 0), §14, §15, §17; CLAUDE.md; decisioni A1-A11 in `docs/progress.md`.

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13 + prompt) | Evidenza prevista |
|---|---|
| Soluzione compilabile con tutti i progetti e le interfacce di `Core` | `dotnet build` senza avvisi (avvisi = errori) su Windows e Linux in CI; test `ArchitectureTests` |
| Benchmark Python eseguibile su una cartella di foto, con report dei tempi per fase | Report `benchmark.md` + `benchmark.json` generato in `PHOTOFLOW_TESTDATA/lab/benchmark/<data>/`, riportato nel report di fase |
| D-FINE esportato in ONNX con test di parità superato | `export_dfine.py` (S e M) + test xUnit `Category=Parity` sui due livelli di §14, con il loro output |
| Hook pre-commit dimostrato | Test `Integration` automatico su repository temporaneo + tentativo reale di commit di un'immagine (output nel report) |

## 0.1 Repository e soluzione

### File di radice

| File | Contenuto |
|---|---|
| `.gitignore` | Build, `models/**` tranne `manifest*.json`, cataloghi (`*.pfdb`, `*.pfcatalog`), `*.onnx`/`*.pth`/`*.safetensors`, ambienti Python, `PROMPT_Claude_Code.md` (A11) |
| `.gitattributes` | Fine riga normalizzate; `*.ttf` binari |
| `LICENSE`, `NOTICE` | Apache-2.0; `Copyright 2026 Foyer Labs`; attribuzioni font IBM Plex (OFL) |
| `README.md` | Scopo, stato, requisiti, build, `git config core.hooksPath .githooks`, privacy |
| `global.json` | SDK 10.0.401, `rollForward: latestFeature` (A2) |
| `Directory.Build.props` | `net10.0`, `Nullable` enable, `TreatWarningsAsErrors`, `ImplicitUsings`, `EnforceCodeStyleInBuild` |
| `Directory.Packages.props` | Gestione centralizzata delle versioni (tutte fissate, vedi Dipendenze) |
| `.editorconfig` | Stile C#; intestazione `// SPDX-License-Identifier: Apache-2.0` imposta dall'analizzatore (IDE0073 come errore) |
| `PhotoFlow.slnx` | Soluzione nel nuovo formato XML di .NET 10 |

### Progetti e riferimenti

Tutti `net10.0`. Ogni progetto dipende da `Core`; `App` compone tutto; `Tests` vede tutto.

| Progetto | Contenuto in fase 0 | Pacchetti esterni in fase 0 |
|---|---|---|
| `PhotoFlow.Core` | Interfacce, tipi di supporto, entità di §9, `DefaultAppPaths` (solo BCL) | Nessuno |
| `PhotoFlow.Sources` | Vuoto (fase 1) | — |
| `PhotoFlow.Imaging` | `SkiaImageOperations : IImageOperations` (decodifica JPEG/PNG + ridimensionamento), necessario alla parità di livello 2 | SkiaSharp |
| `PhotoFlow.Metadata` | Vuoto (fase 1) | — |
| `PhotoFlow.Inference` | `OnnxRuntimeInferenceProvider` (solo CPU in fase 0), conversione `NamedTensor` ↔ `OrtValue` senza copie | Microsoft.ML.OnnxRuntime |
| `PhotoFlow.Analysis` | `DFineDetector : IObjectDetector` (adattatore: pre-elaborazione, sessione, post-elaborazione) | — |
| `PhotoFlow.Faces`, `.Tagging`, `.Data` | Vuoti (fasi successive) | — |
| `PhotoFlow.App` | Guscio Avalonia (sotto) | Avalonia, CommunityToolkit.Mvvm, Microsoft.Extensions.*, Serilog |
| `PhotoFlow.Tests` | Test (sotto) | xUnit v3, Avalonia.Headless.XUnit |

### Contenuto di `Core`

**Interfacce di §3**: `ISource`, `IImageHandler`, `IPhotoAnalyzer`, `IBatchStage`, `ITagger`, `IInferenceProvider`, `IInferenceSession`, `IMetadataWriter`, `ISearchIndex`, `IAppPaths`.

**Interfacce aggiuntive** (conseguenza di A3 e della regola "ogni libreria dietro un'interfaccia di Core"; da aggiungere a spec §3 con l'approvazione del piano):
- `IObjectDetector` — interfaccia per funzione (A3), implementata dall'adattatore D-FINE.
- `IImageOperations` — decodifica e ridimensionamento; isola SkiaSharp.

**Membri da definire ora** (§3 li lascia vuoti):
- `IAppPaths`: `ConfigDirectory`, `DataDirectory`, `CatalogPath`, `ModelsDirectory`, `CacheDirectory`, `ThumbnailsDirectory`, `LogsDirectory`, `ProfilesDirectory`, `BackupsDirectory`, `TempDirectory`. Implementazione predefinita sulle cartelle utente del sistema; catalogo e modelli sovrascrivibili da configurazione (D5, §11).
- `ISearchIndex`: firme **provvisorie** (testo, vettori, ricerca per similarità), da rivedere nel piano della fase 3.

**Tipi di supporto**: `SourceItem`, `FileSignature`, `PreviewImage`, `RgbImage`, `PhotoContext`, `SceneProfile`, `AnalyzerResult`, `JobContext`, `StageProgress`, `TaggingLevel`, `TagResult`, `HardwareInfo`, `ModelDescriptor`, `ExecutionTarget`, `NamedTensor`, `MetadataChanges`, `XmpWritePolicy`, `Detection`, `BoundingBox`.

**Entità di §9** come `record` senza comportamento: `Library`, `Asset`, `AssetLocation`, `AssetPair`, `Job`, `JobItem`, `ModelRecord`, `AnalyzerResultRecord`, `EmbeddingRecord`, `AssetGroup`, `GroupMember`, `Decision`, `Correction`, `Tag`, `TagLabel`, `AssetTag`, `CustomTag`, `Face`, `Person`, `PersonPrototype`, `Caption`, `OcrResult`, `EntryListItem`, `XmpSyncState`, `UndoLogEntry`, `SchemaVersion`; enumerazioni (`JobType`, `CullingOutcome`, `FaceMode`, `DateSource`, `AssetTagState`, `ModelStatus`, `GroupType`…).

### `PhotoFlow.App` — guscio senza contenuto

- `Tokens.axaml`: un unico `ResourceDictionary` con `ThemeDictionaries` Dark/Light; tutti i colori di §12bis (finestra, esiti, accento), tipografia (7 stili), scala spaziature 4-48, raggi, ombra unica. Nessun colore nelle viste.
- Font IBM Plex Sans/Mono incorporati come risorse `avares` (TTF statici, licenza OFL inclusa), `tnum` dove cambiano numeri.
- Guscio: barra titolo 32 nell'area client estesa con il campo "Cerca ovunque · Ctrl K" (segnaposto), navigazione 184 comprimibile a 48 (automatica sotto 1280 px) con le sezioni della tavola 04, barra strumenti 40, area contenuto vuota, barra di stato 24.
- Tabella dei comandi globale (Ctrl 1-7 sezioni, Ctrl K, Ctrl ,): da essa si generano `KeyBindings` e voci della barra di stato. Le sezioni sono pagine vuote.
- Testi in `Strings.resx` (IT) + `Strings.en.resx` (EN); lingua di sistema per default (D10).
- DI con `Microsoft.Extensions.DependencyInjection`; log `ILogger` → Serilog su file a rotazione in `IAppPaths.LogsDirectory`.

### Hook pre-commit (A7)

- `.githooks/pre-commit` → `scripts/check-images.sh` (sh POSIX, senza dipendenze), usato anche dalla CI.
- Rilevamento per estensione (immagini e RAW: jpg, jpeg, png, gif, webp, bmp, tif, tiff, heic, heif, avif, nef, nrw, cr2, cr3, crw, arw, srf, sr2, raf, dng, orf, rw2, pef, srw, x3f, 3fr, iiq, rwl, erf, mef, mos, kdc) **e** per firma binaria (JPEG, PNG, GIF, WebP, BMP, TIFF/RAW `II*`/`MM*`, ISO-BMFF `ftyp` heic/heif/avif/crx, RAF, ORF, RW2…).
- Ammessi: `tests/data/public/**` solo se elencati in `tests/data/public/LICENSES.md`; percorsi esatti in `.githooks/allowed-images.txt` (oggi solo l'icona `src/PhotoFlow.App/Assets/photoflow.ico`).

## 0.2 CI (GitHub Actions)

- `.github/workflows/ci.yml`, su push e pull request; azioni fissate per SHA del commit.
- Job `build-test`, matrice `windows-latest` + `ubuntu-latest`: `setup-dotnet` da `global.json`, `dotnet build -c Release`, `dotnet test --filter "Category=Unit|Category=Integration|Category=Scenario"`.
- Job `hygiene`: `scripts/check-images.sh --all` su tutti i file tracciati (blocca anche i commit fatti con `--no-verify`).
- Le categorie locali (`Parity`, `Quality`, `Performance`) si saltano da sole senza `PHOTOFLOW_TESTDATA` (`Assert.Skip` di xUnit v3).

## 0.3 Laboratorio Python (`tools/lab`, uv)

Un solo progetto uv, Python 3.12. Tutti gli output e i modelli in `PHOTOFLOW_TESTDATA` (`E:\AI\PhotoFlowTestData`); ogni script **rifiuta** una cartella di output dentro il repository.

| Pacchetto | Versione | Motivo |
|---|---|---|
| torch / torchvision | 2.14.1 / 0.29.1, indice `cu130` | Riferimenti PyTorch; CUDA 13 coerente con onnxruntime-gpu |
| transformers | **4.55.x** (fissata) | Unica finestra compatibile con D-FINE (≥ 4.52.1) e con il codice originale di RAM++ (≤ 4.55) |
| onnxruntime-gpu | 1.31.0 | Benchmark CUDA + CPU degli stessi ONNX usati in C# |
| onnx, numpy, pillow, huggingface_hub | correnti | Export, verifica, download con commit fissato |
| recognize-anything (git, extra `ram`) + timm 0.4.12, fairscale 0.4.4 | fissate | Solo per `verify_ramplus.py` |

Niente `insightface` (dipende da `onnxruntime` CPU, in conflitto con `onnxruntime-gpu`): buffalo_l si esegue direttamente dai suoi file ONNX. Niente `optimum` (vincoli incompatibili): l'export usa `torch.onnx`. Niente PyExifTool: exiftool via `-stay_open` in subprocess.

| Script | Funzione |
|---|---|
| `fetch_models.py` | Scarica i modelli del manifest in `PHOTOFLOW_TESTDATA/models`, verifica SHA-256; buffalo_l solo con `--accept-license buffalo_l` (CLAUDE.md §6) |
| `benchmark.py` | Cartella di foto → tempi per fase: estrazione anteprima (exiftool), D-FINE S/M, volti buffalo_l (rilevamento + 106 landmark + impronta per volto), CLIP immagine, RAM++ int8; ciascuna su CUDA e CPU (ONNX Runtime), riscaldamento escluso; media/p50/p95, foto/s; report `.md` + `.json` con hardware e versioni |
| `export_dfine.py` | Export S e M da `ustc-community/dfine-{small,medium}-obj2coco` (commit fissati), stesso contratto I/O di onnx-community (`pixel_values` → `logits`, `pred_boxes`), opset 17, controllo `onnx.checker` + confronto immediato con PyTorch |
| `parity_refs.py` | Per ogni immagine del set: tensore d'ingresso del preprocessore ufficiale (backend PIL fissato), uscite grezze PyTorch, rilevamenti post-elaborati (soglia 0,5) → `PHOTOFLOW_TESTDATA/parity/dfine-{s,m}/`; prima verifica in Python di nostro export e file onnx-community |
| `verify_ramplus.py` | "Verifica RAM++" di §13: RAM++ originale PyTorch vs ONNX int8 della community sullo stesso set; accordo degli insiemi di tag alle soglie ufficiali, report |

`exiftool` per il laboratorio: `exiftool-13.59_64.zip` da exiftool.org con verifica SHA-256 (`44b512b2…`), estratto in `PHOTOFLOW_TESTDATA/tools/exiftool` (nessuna installazione di sistema).

## 0.4 Manifest dei modelli

`models/manifest.json` + `models/manifest.schema.json`. Per ogni voce: id, funzione, nome, versione/commit, file (URL fissato al commit, SHA-256, dimensione, `mirrors`), licenza (SPDX, URL della fonte, uso commerciale, accettazione richiesta), adattatore previsto, note.

| Voce | File | Licenza | Fonte verificata |
|---|---|---|---|
| D-FINE S obj2coco | `onnx-community/dfine_s_obj2coco-ONNX@f69c4ca` `onnx/model.onnx` 41.535.197 B, `b9e2e766…` | Apache-2.0 (dal modello base) | HF API |
| D-FINE M obj2coco | `onnx-community/dfine_m_obj2coco-ONNX@a2e1133` `onnx/model.onnx` 78.624.257 B, `347f2fab…` | Apache-2.0 (dal modello base) | HF API |
| buffalo_l | release `model-zoo` `buffalo_l.zip` 288.621.354 B, `80ffe37d…` (digest ufficiale GitHub) | Non commerciale, solo ricerca; accettazione esplicita | README InsightFace |
| YuNet 2023mar | 232.589 B, `8f2383e4…` | **MIT** (non Apache-2.0 come in §15) | LICENSE opencv_zoo |
| SFace 2021dec | 38.696.353 B, `0ba9fbfa…` | Apache-2.0 | LICENSE opencv_zoo |
| CLIP ViT-B/32 | `Xenova/clip-vit-base-patch32@d15189d` vision + text quantizzati (≈ 153 MB, come §5) | MIT (codice OpenAI); vedi nota N1 | HF API |
| RAM++ int8 | `anakhiu/ram-plus-onnx-int8@f5226bd` 872.789.856 B, `44836da6…` + liste tag e soglie | Apache-2.0; provenienza debole (un solo autore, 5 immagini di verifica) | HF API |

## 0.5 Test di parità D-FINE

- `OnnxRuntimeInferenceProvider` (CPU, `ExecutionTarget.Cpu`) carica il modello tramite `IInferenceProvider`.
- `DFineDetector`: ridimensionamento 640×640 senza proporzioni, pixel 0-1, CHW; post-elaborazione sigmoide → top-k 300 → `cxcywh` → `xyxy` scalati all'immagine originale.
- Test `Category=Parity` sui due livelli di §14, per S e M, sia sul nostro export sia sul file onnx-community; report per immagine in `PHOTOFLOW_TESTDATA/parity/reports/`. Saltati se mancano dati o riferimenti.

## 0.6 Verifiche tecniche

Velopack, PanAndZoom, HdbscanSharp, RapidOcrNet, binding .NET di LibRaw (con NEF High Efficiency), DirectML vs Windows ML: risultati con fonti in `progress.md` e proposte di aggiornamento a §15 e §17. Già emerso: vedi note N1-N4.

## Test della fase 0

| Test | Categoria | Verifica |
|---|---|---|
| `ArchitectureTests` | Unit | `Core` senza pacchetti né riferimenti; ONNX Runtime solo in `.Inference`; Avalonia solo in `.App`; SkiaSharp solo in `.Imaging` |
| `SourceHygieneTests` | Unit | Intestazione SPDX in ogni `.cs`; nessun percorso assoluto in `src/` |
| `ManifestTests` | Unit | Schema, SHA-256 a 64 cifre esadecimali, URL https fissati a un commit, licenza presente, non commerciali con accettazione |
| `NamedTensorTests`, `DFinePostprocessingTests` | Unit | Forme e lunghezze; sigmoide, top-k, conversione riquadri su tensori sintetici |
| `ThemeTokenTests` | Unit (headless) | Ogni token presente in Dark e Light con i valori di §12bis |
| `CommandTableTests` | Unit (headless) | Scorciatoie uniche; `KeyBindings` e barra di stato generati dalla stessa tabella |
| `PreCommitHookTests` | Integration | Repository temporaneo: immagine bloccata, JPEG rinominato `.txt` bloccato, file ammesso accettato |
| `DFineParityTests` | Parity | Livelli tensori e rilevamenti |

**Invarianti I1-I5**: nessun codice della fase 0 legge sorgenti, copia file o scrive metadati; i loro test nascono, prima del codice, nella fase 1.

## Dipendenze .NET (versioni stabili verificate su nuget.org il 2026-10-12)

| Pacchetto | Versione | Licenza |
|---|---|---|
| Avalonia, .Desktop, .Themes.Fluent, .Headless.XUnit | 12.1.4 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.ML.OnnxRuntime | 1.31.0 | MIT |
| Microsoft.Extensions.DependencyInjection / Logging / Hosting | 10.0.12 | MIT |
| Serilog / .Extensions.Logging / .Sinks.File | 4.4.0 / 10.0.0 / 7.0.0 | Apache-2.0 |
| SkiaSharp (+ NativeAssets.Linux) | 3.119.4 (allineata ad Avalonia 12) | MIT |
| xunit.v3 / xunit.runner.visualstudio | **3.2.2 / 3.1.5** (le 4.x rompono Avalonia.Headless.XUnit) | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT |
| IBM Plex Sans 1.1.0 / Plex Mono 2.5.0 (TTF) | — | OFL-1.1 |

Non ancora usati in fase 0: System.IO.Hashing 10.0.12, Microsoft.Data.Sqlite 10.0.12, Dapper 2.1.89.

## Note emerse dalla verifica (da decidere nelle fasi indicate, non ora)

| # | Nota | Fase |
|---|---|---|
| N1 | La scheda del modello CLIP di OpenAI dichiara "fuori ambito" ogni uso in prodotti distribuiti, commerciali o no (indicazione d'uso, non licenza: codice e pesi MIT). Valutare alternative OpenCLIP | 2 |
| N2 | `Microsoft.ML.OnnxRuntime.DirectML` è fermo alla 1.24.4 (core 1.31.0); DirectML è "in sustained engineering", lo sviluppo passa a Windows ML. Incide su D-variante Windows predefinita e R5 | 0.6 → proposta per §5/§11 |
| N3 | ONNX Runtime CUDA non ha `GridSample` in fp16: D-FINE fp16 su GPU CUDA ricadrebbe su CPU. Si resta su fp32 | 1 |
| N4 | YuNet ha ingresso fisso 640×640 in ONNX: l'adattatore dovrà ridimensionare con bordi | 1 |

## Rischi

| Rischio | Mitigazione |
|---|---|
| Ridimensionamento SkiaSharp diverso da PIL → parità di livello 2 non superata | Il livello tensori isola ONNX Runtime; se serve, ricampionamento C# equivalente a PIL in `.Imaging` |
| Barra del titolo: Avalonia 12 ha rifatto le decorazioni (`WindowDrawnDecorations`) rispetto alla mappa di §12bis | Verifica all'inizio del lavoro sul guscio; se non fedele, segnalazione con alternativa |
| Export `torch.onnx` di D-FINE (GridSample, opset) | onnx-community resta il file distribuito; il nostro export serve alla parità |
| Installazione del codice originale di RAM++ (dipendenze vecchie) | Extra opzionale `ram`; se non installabile, report "verifica RAM++ non eseguibile" con motivo, senza bloccare i criteri di §13 |
| Download pesanti (~7 GB: torch CUDA, modelli, RAM++ originale 3,5 GB) | Tutto in `PHOTOFLOW_TESTDATA` e cache uv; SHA-256 verificati |

## Sequenza di lavoro e commit

1. Su `main`: primo commit con documenti (CLAUDE.md, spec, tavole, progress, piano), `.gitignore`, `LICENSE`, `NOTICE`, `README`, hook e `check-images.sh` (la protezione esiste prima di qualsiasi altro file).
2. Branch `phase/0-setup`: soluzione e Core → hook test → CI → App e token → Inference/Analysis/Imaging → manifest → laboratorio → parità → verifiche 0.6.
3. Commit piccoli, autore Foyer Labs. Fine fase: report con checklist ed evidenze, pull request `phase/0-setup` → `main` da approvare.

## Cosa serve dall'utente

- 30-50 foto varie per la parità e ~200 per il benchmark (NEF della Z5 II + JPG) in `E:\AI\PhotoFlowTestData\photos\` (fuori dal repository).
