# Piano della fase 1 — Import, analisi base, XMP, catalogo

Stato: **approvato** il 2026-10-12. Branch: `phase/1-import`.

Riferimenti: spec §3, §4, §6 (segnali, esiti), §7 (rilevamento e occhi), §9, §10, §12bis, tavole 3a-3i e 2a, P9, P10, P11, P19, P20; decisioni A5, A6, A9, T1; rischi R13, R15-R17, R19.

## Contenuto (spec §13)

Sorgenti SD + cartella; gestori RAW/JPG; ingest con copia verificata; esposizione/nitidezza; rilevamento volti e occhi; politiche XMP; catalogo base; procedura di import e riepilogo con griglia; avanzamento per foto (`JobItems`) e ripresa dei job di import (A9).

**Fuori dalla fase 1** (non anticipare): raffiche e sequenze, CLIP, profilo automatico (fase 2); riconoscimento e clustering volti, Persone (fase 2); tag, keyword, vocabolario (fase 3); OCR (fase 4); TIFF/PNG/HEIC, job d'archivio in background, bassa priorità, hash completo in background (fase 1b).

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13) | Evidenza prevista |
|---|---|
| Import di 1.000 NEF: zero file persi, tutti verificati | Test `Scenario` con 1.000 file sintetici con firma NEF (CI) + test `Performance` locale sui 178 NEF reali; conteggi e impronte sorgente/destinazione |
| Secondo import della stessa scheda: nessun duplicato | Test `Scenario` (stessa cartella-SD importata due volte) |
| Chiusura forzata a metà: ripresa dal punto di interruzione | Test `Scenario` che termina il processo di import (`ScenarioHost`) a metà e lo riavvia: nessuna foto già completata rielaborata, nessun file perso |
| SD in sola lettura: nessun tentativo di scrittura o cancellazione | Test `Scenario` con sorgente in sola lettura + file system registrante (`IFileSystem`) che fallisce a qualunque scrittura sotto la sorgente + impronte prima/dopo (I1) |
| RAW invariati dopo la scrittura XMP | Test `Integration` con exiftool su RAW pubblici CC0 (impronta prima/dopo, solo sidecar) (I2) |
| XMP riletto correttamente da exiftool; checklist manuale in Esplora risorse e Photoshop/Camera Raw | Test `Integration` scrittura → rilettura per le tre politiche; checklist manuale per l'utente |
| Set di foto mosse/occhi chiusi: percentuale di intercettazione misurata | Test `Quality` locale su cartelle etichettate dall'utente; report con intervallo di confidenza |

## Tappe

Le parti senza interfaccia precedono quelle con interfaccia (§13). Ogni tappa termina con test verdi e commit.

| Tappa | Contenuto | Progetti |
|---|---|---|
| 1.0 | **Test delle invarianti I1-I4 scritti per primi** (falliscono finché il codice non esiste) + infrastruttura `IFileSystem` registrante e sorgenti simulate | Tests, Core |
| 1.1 | Catalogo: SQLite, migrazione `0001_initial.sql`, backup prima di ogni migrazione, file di lock, modalità journal per percorsi di rete, repository | Data |
| 1.2 | exiftool: servizio `-stay_open` con controllo di salute e riavvio (R13), lettura EXIF (§4), scrittura/lettura XMP con le tre politiche, unione mai sovrascrittura | Metadata |
| 1.3 | Sorgenti: rilevamento SD (polling ~2 s dei volumi con `DCIM`), cartella, scansione con firma di contenuto, coppie RAW+JPG, video | Sources |
| 1.4 | Ingest: copia a lettura singola con scrittura parallela e xxHash3 in lettura, scrittura su `.pftmp` e rinomina dopo verifica (A9), destinazioni locali + NAS asincrona con retry, controllo spazio, pausa per destinazione scollegata (3i), template percorsi (P20), riconoscimento già importate e opzione "Tutte" (P9) | Sources |
| 1.5 | Gestori di formato RAW (anteprima incorporata via exiftool, ripiego Magick.NET/LibRaw, NEF HE solo anteprima) e JPG (SkiaSharp), scelta dal contenuto; cache miniature | Imaging |
| 1.6 | Inferenza: provider CUDA (ORT 1.31) + CPU, pacchetto GPU (CUDA/cuDNN) scaricato al primo uso con SHA-256, rilevamento hardware e micro-benchmark, registro modelli dal manifest, download con SHA-256 e accettazione licenza (P11); parità D-FINE anche su CUDA | Inference |
| 1.7 | Analisi: esposizione, nitidezza (zona del profilo: punto AF → volti → soggetto → riquadri), soggetto (D-FINE), volti + occhi (buffalo_l: SCRFD + 106 landmark; YuNet: occhi sconosciuti, A5), decisione fase 1 (scarto solo su segnale certo, da rivedere, tenuta; modalità prudente D20); profili predefiniti JSON versionati | Analysis, Faces |
| 1.8 | Pipeline: fasi per foto con `Channel` limitati, job runner con `JobItems` e ripresa, rilevamento chiusura imprevista | Core, App |
| 1.9 | Interfaccia (layout completo delle tavole, funzioni future disattivate, F1-6): Home (3a, 3b primo avvio P11, 3c ripresa), Import 3 passi (3d-3f, P9, P10), Job (3g-3i), griglia di Revisione 2a con esiti, P/X/stelle e Ctrl+Z, conferma e scrittura XMP (2g, 2h) | App |
| 1.10 | Misure: test `Quality` (mosse/occhi chiusi), `Performance` (1.000 NEF), checklist manuale XMP | Tests |

## Interfacce nuove o modificate in Core

| Interfaccia | Scopo |
|---|---|
| `IFileSystem` | Unico accesso ai file per sorgenti, ingest e metadati: rende verificabile I1 ("nessun tentativo di scrittura") |
| `ISourceDetector` | Rilevamento schede (polling volumi rimovibili con `DCIM`) |
| `IIngestService` / `ICopyEngine` | Copia verificata multi-destinazione con avanzamento e pausa |
| `IPathTemplate` | Template percorsi (P20), caratteri non validi → "-" |
| `IExifReader` | EXIF di §4 (incluso `SubSecDateTimeOriginal`, punto AF) |
| `IMetadataWriter` | Già in §3: implementazione exiftool con le tre politiche |
| `ICatalog` + repository | Accesso al catalogo; nessun SQL fuori da `PhotoFlow.Data` |
| `IModelRegistry`, `IModelDownloader` | Manifest, stato dei modelli, download verificato, accettazione licenze |
| `IFaceDetector` | Interfaccia per funzione (A3): riquadri, 5 punti, landmark densi se disponibili |
| `IDecisionEngine` | Esito proposto dalle misure e dal profilo |
| `IJobRunner` | Esecuzione, pausa, ripresa dei job |

## Test (invarianti per primi)

| Test | Categoria | Verifica |
|---|---|---|
| `SourceIsNeverWrittenTests` (I1) | Scenario | Sorgente sola lettura, `IFileSystem` registrante, impronte prima/dopo, conteggio file |
| `RawIsNeverModifiedTests` (I2) | Integration | Impronta dei RAW prima/dopo scrittura XMP con ogni politica; solo sidecar |
| `NoStageBeforeVerificationTests` (I3) | Scenario | Iniezione di errori nella copia (byte alterati, destinazione che sparisce): nessuna fase successiva sul file |
| `NothingIsDeletedTests` (I4) | Scenario | Nessuna cancellazione in sorgente e destinazioni, anche con errori e riprese; `.pftmp` sovrascritti, mai eliminati |
| `ManualDecisionsWinTests` (I5) | Unit | P/X/stelle durante l'analisi prevalgono sulla proposta (P2) |
| Ingest | Unit + Scenario | Template, già importate (P9), coppie RAW+JPG, video, spazio insufficiente, NAS assente, scheda scollegata |
| Catalogo | Integration | Migrazioni, backup, lock, ripresa da `JobItems` |
| Analisi | Unit + Parity | Esposizione e nitidezza su immagini sintetiche; SCRFD/2d106det contro riferimenti Python (`tools/lab`) |
| `ImportScenarioTests` | Scenario | 1.000 file, doppio import, chiusura forzata (processo `ScenarioHost`) |
| `CullingQualityTests` | Quality | Intercettazione mosse/occhi chiusi su set etichettato |
| Interfaccia | Unit (headless) | Tabelle comandi delle viste, navigazione da tastiera, stati vuoto/errore |

Dati pubblici di test: pochi RAW con licenza CC0 da raw.pixls.us (es. Nikon D40, D5100) e JPG CC0 in `tests/data/public/` con `LICENSES.md`.

## Dipendenze nuove (versioni e licenze verificate 2026-10-12)

| Componente | Versione | Licenza | Progetto |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.12 | MIT | Data |
| Dapper | 2.1.89 | Apache-2.0 | Data |
| System.IO.Hashing (xxHash3) | 10.0.12 | MIT | Sources |
| exiftool | 13.59 | Artistic/GPL (eseguibile separato, scaricato in build con SHA-256) | Metadata |
| Microsoft.ML.OnnxRuntime.Gpu | 1.31.0 (CUDA 13, cuDNN 9) | MIT | Inference |
| Librerie NVIDIA CUDA/cuDNN (pacchetto GPU) | CUDA 13.x, cuDNN 9.x | Licenze NVIDIA di ridistribuzione (da verificare per componente nella tappa 1.6) | scaricate al primo uso, mai nel repository |
| Magick.NET-Q16-AnyCPU | 15.0.0 (LibRaw 0.22.2) | Apache-2.0; LGPL statiche (R19, revisione prima della distribuzione) | Imaging |

## Decisioni prese (2026-10-12)

| # | Tema | Decisione |
|---|---|---|
| F1-1 | NEF Z5 II non disponibili | Verifica rinviata a dopo l'implementazione (checklist manuale); NEF Z5 II pubblici con licenza chiara se disponibili. spec §13 |
| F1-2 | Backend di inferenza | CUDA (ORT 1.31) per NVIDIA + CPU per le altre; librerie CUDA/cuDNN come pacchetto GPU scaricato al primo uso (SHA-256, importabile offline). DirectML escluso: risultati errati con D-FINE e crash con CLIP int8 sulla RTX 3090; Windows ML nel backlog, ammesso solo dopo la parità. spec §3, §5, §11, §16, §17; CLAUDE.md §3 |
| F1-3 | Decodifica RAW di riserva | Magick.NET 15 (LibRaw 0.22.2) già in fase 1; provato sui NEF D40/D5100: decodifica corretta, file troncati o non validi → eccezione gestita. spec §3, §4 |
| F1-4 | Analisi immagine | C# puro, con parità contro OpenCV calcolata nel laboratorio; OpenCvSharp non adottato (runtime macOS fermi a OpenCV 4.6-4.8). spec §3, §15 |
| F1-5 | exiftool | Scaricato in build (13.59, SHA-256), copiato accanto all'app, percorso personalizzabile. spec §11 |
| F1-6 | Interfaccia | Layout completo delle tavole; controlli delle fasi successive presenti ma disattivati con "Arriva con la fase N". spec §13 |
| F1-7 | Set etichettato | Cartelle `quality/buone` (≥ 100), `quality/mosse` (30-50), `quality/occhi_chiusi` (30-50) in `PHOTOFLOW_TESTDATA`, entro la tappa 1.10. spec §14 |

Misure a supporto di F1-2 (230 foto reali, ms/foto, RTX 3090): D-FINE S CUDA 13,3 / DirectML 22,9; D-FINE M 17,0 / 27,6; volti 22,7 / 24,5; RAM++ int8 543 / 52,5; CLIP quantizzato 26 / crash. D-FINE su DirectML: punteggi 0,35 invece di 0,93 e riquadri con dimensioni negative.

## Rischi

| Rischio | Mitigazione |
|---|---|
| Fase ampia | Tappe con test e commit; parti senza interfaccia prima |
| Pacchetto GPU CUDA/cuDNN (dimensione, licenze di ridistribuzione) | Facoltativo, verificato con SHA-256; CPU sempre disponibile; licenze verificate per componente |
| Scrittura XMP incorporata nei JPG (riscrittura del file) | exiftool scrive su temporaneo; copia `_original` configurabile; verifica con rilettura |
| Prestazioni anteprime NEF (70 ms/foto misurati) | Pipeline parallela; anteprime ridotte in cache |
| NEF HE (R15) | Solo anteprima incorporata, stato dedicato |
| Tempo di sviluppo | Commit piccoli, report a fine fase |
