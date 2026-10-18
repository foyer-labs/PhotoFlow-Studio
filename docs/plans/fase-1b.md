# Piano della fase 1b — Archivi, formati, job in background

Stato: **approvato** il 2026-10-12. Branch: `phase/1b-archive`.

Riferimenti: spec §3 (pipeline, tipi di job), §4 (gestori, politiche XMP, D21), §10 (invarianti, I4 dopo B1, archivi grandi), §13 (fase 1b), §14 (dati di test); D4, D21; P8; tavole 3a, 3g, Librerie e Impostazioni (gruppo C); decisione B1.

## Contenuto (spec §13)

Gestori TIFF/PNG/HEIC (e WebP, D4); analisi sul posto degli archivi esistenti (`AnalyzeInPlace`) con ripresa; più job in parallelo con pausa/ripresa; bassa priorità; hash rapido al primo passaggio e completo in background. In più, ciò che la fase 1 ha rinviato alla 1b: regole di pulizia dei file interni (B1), sezioni Librerie e Impostazioni essenziali, importazione offline del pacchetto modelli.

**Fuori dalla fase 1b** (non anticipare): raffiche, CLIP, Revisione completa, riconoscimento volti (fase 2); tag, ricerca, duplicati (fase 3).

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13) | Evidenza prevista |
|---|---|
| Analisi di 10.000 foto miste interrotta e ripresa senza ripetere il lavoro | Test `Scenario` con 10.000 file sintetici di formati misti, processo `ScenarioHost` terminato a metà e ripreso: nessuna foto completata rielaborata, nessun file della libreria modificato (solo sidecar con `SidecarOnly`). Misura locale sul subset d'archivio (`PHOTOFLOW_TESTDATA/archive`) |
| File con estensione sbagliata gestiti dal gestore corretto | Test `Integration`: JPEG chiamato `.png`, TIFF chiamato `.jpg`, HEIC chiamato `.jpg`, ecc. (campioni con licenza libera in `tests/data/public/`) |
| File corrotti nel report, intatti | Test `Integration` e `Scenario`: file troncati e non validi di ogni formato → voce nel report del job, impronta identica prima/dopo, il job continua |

## Tappe

| Tappa | Contenuto | Progetti |
|---|---|---|
| 1b.0 | Test prima del codice: I4 dopo B1 (nessuna cancellazione fuori dalle cartelle interne), I2/I5 per `AnalyzeInPlace` (solo sidecar con `SidecarOnly`, decisioni manuali intatte), ripresa del job d'archivio | Tests |
| 1b.1 | Gestori: PNG e WebP (SkiaSharp), TIFF (Magick.NET: 16 bit, multipagina → prima pagina, file grandi con riduzione), HEIC (Magick.NET/libheif); scelta dal contenuto; campioni pubblici con licenza verificata | Imaging |
| 1b.2 | Identità: hash rapido al primo passaggio, hash completo in background (catalogo + job a bassa priorità); riconoscimento delle foto già nel catalogo | Sources, Data |
| 1b.3 | Job `AnalyzeInPlace`: libreria esistente con `SidecarOnly` (D21), scansione per cartella o dalla più recente, fasi 2-9 senza copia, `JobItems` e ripresa; sorgenti lente con coda limitata | Sources, Core |
| 1b.4 | Più job in parallelo: priorità (l'import ha precedenza sulla GPU), bassa priorità con limiti di CPU/memoria, pausa/ripresa per job, stima corretta in corsa | App, Core |
| 1b.5 | Pulizia dei file interni (B1): ultimi N backup del catalogo, cache miniature con limite di spazio, temporanei, archivi scaricati dopo l'installazione verificata | Data, Imaging, App |
| 1b.6 | Interfaccia: sezione Librerie (aggiungi archivio, analizza in background, politica XMP con conferma D21), Impostazioni essenziali (catalogo e backup, modalità prudente, template, exiftool, modelli e pacchetto offline), job in parallelo nella vista Job (3g) | App |
| 1b.7 | Misure: subset d'archivio, scenario 10.000 file, formati e file corrotti; report di fine fase | Tests |

## Rischi

| Rischio | Mitigazione |
|---|---|
| HEIC: brevetti HEVC e LGPL statiche (libheif, libde265; R19) | Decodifica tramite Magick.NET già in uso; verifica legale prima della prima versione distribuita (già in R19); se servisse, HEIC disattivabile |
| Archivi su NAS lenti o intermittenti | Coda limitata, pausa per sorgente non raggiungibile (come P8), ripresa |
| Contesa GPU/CPU tra import e archivio | Priorità esplicita e limiti; misura con due job insieme |
| Cancellazioni dopo B1 | Solo dentro le cartelle di `IAppPaths`, con test che le vieta altrove |

## Dati

Subset d'archivio in `E:\AI\PhotoFlowTestData\archive` (1.000-2.000 foto, preparato dall'utente, solo lettura). Campioni di formato pubblici con licenza libera in `tests/data/public/` (`LICENSES.md`), scaricati dai test come i NEF CC0.
