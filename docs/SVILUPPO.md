# PhotoFlow per sviluppatori

Applicazione desktop in C#/.NET con interfaccia Avalonia: importa le foto dalle schede SD, le sceglie in automatico, le tagga, riconosce volti e numeri di gara e cerca in archivi grandi, con modelli di intelligenza artificiale locali (ONNX Runtime). Nessun cloud, nessuna telemetria.

Torna al [README](../README.md).

## Documenti di riferimento

| Documento | Contenuto |
|---|---|
| [`docs/spec.md`](spec.md) | Specifica completa e vincolante (in italiano): decisioni, architettura, fasi e criteri di accettazione |
| [`CLAUDE.md`](../CLAUDE.md) | Regole permanenti del repository: invarianti, confini dell'architettura, convenzioni |
| `docs/plans/`, `docs/reports/` | Piani e report di ogni fase |
| `docs/design/*.html` | Tavole di design: riferimento visivo, non codice da riutilizzare |
| [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) | Componenti di terze parti e licenze |

## Principi

- **Nessuna perdita di dati**: la sorgente dell'import non viene mai scritta, i RAW non vengono mai modificati (solo sidecar XMP), nessun file viene mai cancellato in automatico.
- **Vincono le azioni dell'utente**: tag e decisioni manuali non vengono mai sovrascritti dai processi automatici.
- **Interoperabilità standard**: i dati stanno in un catalogo locale e sono sempre esportati in XMP standard.

Le invarianti I1-I5 sono elencate in `CLAUDE.md` e sono sempre coperte da test, scritti prima del codice che le riguarda.

## Requisiti

- .NET SDK 10 (versione fissata in `global.json`).
- Windows 10/11 x64 è la piattaforma principale. La CI compila e testa anche su Linux.
- Facoltativo, per il laboratorio Python in `tools/lab`: [uv](https://docs.astral.sh/uv/) e una GPU NVIDIA.

## Struttura del repository

```
docs/            spec.md, design/, plans/, reports/, guida/ (guida utente e screenshot)
src/             PhotoFlow.Core, .Sources, .Imaging, .Metadata, .Inference,
                 .Analysis, .Faces, .Tagging, .Data, .Vlm, .App
tests/           PhotoFlow.Tests, PhotoFlow.ScenarioHost, data/public/
tools/lab/       Python: benchmark, export ONNX, riferimenti di parità (uv)
tools/vlm/       Conversione di Qwen2.5-VL-7B per le didascalie in italiano
models/          manifest.json (MAI i file dei modelli)
profiles/        profili di scena predefiniti (JSON versionati)
vocabulary/      vocabolario dei tag e lista nera
build/           target MSBuild (exiftool scaricato in fase di build)
scripts/         controllo delle immagini usato da hook e CI
```

`PhotoFlow.Core` non ha dipendenze esterne: ogni libreria esterna sta dietro un'interfaccia di `Core`, e tutti i percorsi passano da `IAppPaths`.

## Build e test

```bash
dotnet build
dotnet test --filter Category=Unit
dotnet test --filter "Category=Unit|Category=Integration|Category=Scenario"
```

Gli avvisi sono trattati come errori. La CI (`.github/workflows/ci.yml`) esegue la build in Release e le categorie `Unit`, `Integration` e `Scenario` su Windows e Linux.

### Categorie di test

Ogni test ha il trait xUnit `Category`.

| Categoria | Dove gira | Dati |
|---|---|---|
| `Unit` | Ovunque, CI | Nessun file |
| `Parity` | Locale | Riferimenti in cartella esterna |
| `Integration` | Ovunque, CI | File sintetici o con licenza libera |
| `Scenario` | Ovunque, CI | Cartelle simulate |
| `Quality` | Solo locale | Foto personali etichettate |
| `Performance` | Solo locale | Subset d'archivio |

### Variabili d'ambiente

| Variabile | Uso |
|---|---|
| `PHOTOFLOW_TESTDATA` | Cartella dei dati locali (`Parity`, `Quality`, `Performance`), fuori dal repository. Se non è impostata, questi test vengono **saltati**, non falliscono |
| `PHOTOFLOW_SAMPLES` | Cache dei campioni pubblici scaricati e verificati con SHA-256 (predefinita: `%LOCALAPPDATA%\PhotoFlow.TestSamples`) |
| `PHOTOFLOW_DOCS_SCREENSHOTS` | Cartella di uscita degli screenshot della guida, generati da `tests/PhotoFlow.Tests/Docs/DocsScreenshots.cs` su un catalogo dimostrativo di immagini disegnate. Se non è impostata, il test viene saltato |

## Contribuire: attiva l'hook pre-commit

Il repository è pubblico: le foto personali non devono mai essere committate. Dopo il clone attiva una volta gli hook versionati:

```bash
git config core.hooksPath .githooks
```

L'hook rifiuta immagini e file RAW (per estensione e per firma del file) fuori da `tests/data/public/`, che contiene solo file con licenza libera verificata, elencati in `tests/data/public/LICENSES.md`. Le poche immagini prodotte per il progetto (icona, screenshot della guida) sono elencate una per una in `.githooks/allowed-images.txt`. La CI esegue lo stesso controllo su tutti i file.

Altre regole: nessun volto reale, nessun dato del catalogo, nessuna chiave nel repository; intestazione `// SPDX-License-Identifier: Apache-2.0` in ogni file sorgente; codice e commenti in inglese, testi dell'interfaccia solo nei file di risorse IT/EN.

## Modelli

I file dei modelli non entrano mai nel repository. `models/manifest.json` elenca ogni modello con URL di origine, SHA-256, dimensione e licenza; l'applicazione li scarica al primo uso, dopo aver mostrato la licenza. I modelli non commerciali (buffalo_l) richiedono un'accettazione esplicita.

Ogni modello ha un adattatore di input/output e un test di parità contro i riferimenti generati in `tools/lab/` (vedi [`tools/lab/README.md`](../tools/lab/README.md)). Le didascalie in italiano usano Qwen2.5-VL-7B convertito sul PC dell'utente con gli script di [`tools/vlm/`](../tools/vlm/README.md), in un processo separato (`PhotoFlow.Vlm`).

## Pacchetto e release

Il programma è distribuito come pacchetto self-contained con Velopack (id `FoyerLabs.PhotoFlow`). Durante lo sviluppo si esegue dalla cartella di build; l'installer si genera solo alle versioni di riferimento, pubblicate come release `vX.Y.Z` di questo repository. Dettagli in `docs/spec.md` §11.

## Licenza

Apache License 2.0: vedi [`LICENSE`](../LICENSE) e [`NOTICE`](../NOTICE). Componenti di terze parti in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).
