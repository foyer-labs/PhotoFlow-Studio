# Report di fine fase 1b — Archivi, formati, job in background

Data: 2026-10-13 · Branch: `phase/1b-archive` · Piano: [`docs/plans/fase-1b.md`](../plans/fase-1b.md)

Fase svolta in autonomia su indicazione dell'utente (2026-10-12): le decisioni prese durante la fase sono in `docs/progress.md` e nella specifica, e sono riassunte sotto.

## Checklist dei criteri di accettazione (§13)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Analisi di 10.000 foto miste interrotta e ripresa senza ripetere il lavoro | ✅ Superato | `ArchiveScenarioTests.Ten_thousand_mixed_files_killed_and_resumed_without_redoing_work`: 10.000 file (JPEG, 1.500 gemelli RAW+JPEG, PNG, TIFF, WebP, HEIC, 80 estensioni sbagliate, 30 danneggiati) analizzati sul posto con l'analisi reale (gestori di formato e segnali del culling, senza modelli) nel processo separato `PhotoFlow.ScenarioHost`, terminato dopo 2.500 file e riavviato: job rilevato come interrotto, ripreso e completato; nessun file completato prima dell'interruzione rielaborato; ogni foto con decisione; ogni asset con l'hash completo; cartella identica byte per byte, nessun file aggiunto. Anche `ArchiveJobTests` (pausa utente e ripresa, decisioni manuali intatte, foto già analizzate non rifatte) |
| 2 | File con estensione sbagliata gestiti dal gestore corretto | ✅ Superato | `FormatHandlerTests.A_wrong_extension_still_reaches_the_right_handler` (JPEG `.png`, TIFF 16 bit `.jpg`, HEIC `.jpg`, PNG `.tif`); nello scenario da 10.000: 30 PNG `.JPG`, 30 JPEG `.png`, 20 TIFF `.jpg` analizzati e registrati con il gestore del contenuto |
| 3 | File corrotti nel report, intatti | ✅ Superato | `FormatHandlerTests.A_truncated_file_raises_a_decoding_error_and_stays_intact` (JPEG, PNG, WebP, TIFF, HEIC troncati) e `A_file_that_is_not_an_image_is_rejected_by_content`; nello scenario: i 30 danneggiati (marcatore JPEG + rumore, PNG tagliati, testo chiamato `.png`), **e solo loro**, sono `Failed` con il messaggio d'errore; impronte invariate; il job continua |

Test: 327, di cui 24 locali (parità, qualità, prestazioni) saltati senza `PHOTOFLOW_TESTDATA`; gli altri girano in CI su Windows e Linux. Lo scenario da 10.000 file dura ~4 minuti in locale.

## Cosa è stato fatto (tappe 1b.0-1b.7)

| Tappa | Contenuto |
|---|---|
| 1b.0 | Test prima del codice: I4 dopo B1 (`InternalCleanupTests`: cancellazioni solo dentro le cartelle interne di `IAppPaths`, rifiutati i collegamenti che puntano fuori; `NoDeleteInProductCodeTests` aggiornato: un solo punto di cancellazione nel codice), I2/I5 per l'analisi sul posto, ripresa del job d'archivio |
| 1b.1 | Gestori: PNG e WebP (SkiaSharp; file incompleti = errore; trasparenza su bianco), TIFF (Magick.NET: prima pagina, 16 bit → 8 bit), HEIF/HEIC/AVIF (Magick.NET); scelta dal contenuto. Campione HEIC sintetico CC0 in `tests/data/public/` (generato con `tools/lab/make_heic.py`) |
| 1b.2-1b.3 | Job `AnalyzeInPlace`: file solo letti, ordine "dalle più recenti" o per cartella, gemelli RAW+JPEG insieme, hash rapido al primo passaggio e passata finale dell'hash completo (riprendibile), foto già nel catalogo riconosciute e non rianalizzate, `JobItems` e ripresa, pausa se la libreria sparisce (disco o NAS scollegato) |
| 1b.4 | Più job in parallelo: l'import ha la precedenza; l'analisi d'archivio usa metà delle analisi in parallelo e attende mentre gira un import; pausa e ripresa per job |
| 1b.5 | File interni (B1): backup del catalogo alla chiusura e almeno giornaliero (ultimi 10), cache miniature entro 4 GB (le più vecchie, ricostruibili), temporanei oltre 24 h, archivi del pacchetto GPU dopo l'installazione verificata |
| 1b.6 | Interfaccia: **Librerie** (5a, 5b2: aggiungi cartella esistente → `SidecarOnly`, prima analisi sul posto, politica XMP con conferma D21 che indica il numero di file non RAW, sola lettura); **Impostazioni** a schede (5f-5j: hardware e pacchetto GPU, modelli con licenza/spazio/stato/download/verifica, **pacchetto offline**, catalogo con backup ora e **ripristino**, lingua, tema, exiftool, template dei percorsi, modalità del culling); vista **Job** con job in parallelo e lavoro in background; pagina di ripristino all'avvio con catalogo danneggiato |
| 1b.7 | Scenario da 10.000 file con processo terminato e ripreso; misura locale in modalità background; report |

## Misure

**Analisi d'archivio in background** (`ArchivePerformanceTests`, Release, 24 thread + RTX 3090, CUDA, 4 analisi in parallelo = metà dell'import, D-FINE M + volti/occhi buffalo\_l in rilevamento + decisione):

| Misura | Valore |
|---|---|
| Insieme | `PHOTOFLOW_TESTDATA/photos`: 230 file, 2,51 GB (178 NEF, 52 JPEG) |
| EXIF + hash rapido + anteprima + analisi + decisione | 27,9 s → **8,2 foto/s** |
| Passata dell'hash completo | 4,9 s (~510 MB/s) |
| Stima per 57.130 foto (archivio della tavola 5f) | **~1,9 h** |
| File danneggiati trovati | 0 |

Il subset d'archivio (`PHOTOFLOW_TESTDATA/archive`) non era ancora presente: la misura ha usato il set `photos` e si ripete da sola sul subset appena viene copiato (vedi sotto).

**Scenario sintetico** (10.000 file piccoli, analisi senza modelli): ~50 file/s; il limite è il catalogo (~11 scritture per foto, ciascuna con il proprio commit). Con i modelli il collo di bottiglia resta l'analisi (8-11 foto/s), quindi non serve intervenire ora; raggruppare le scritture per foto in una transazione è nel backlog (§17).

## Decisioni tecniche prese durante la fase

Registrate in `docs/progress.md` e nella specifica (§9, §17):

- **Ripristino del catalogo**: scelto nelle Impostazioni, si applica **al prossimo avvio** (il catalogo è aperto per tutta la sessione); il catalogo sostituito si sposta in `backup/replaced-*` e non si cancella; il backup scelto resta fuori dalla rotazione; un backup danneggiato viene rifiutato senza toccare nulla. All'avvio con catalogo danneggiato una pagina propone il ripristino dell'ultimo backup (§9).
- **Pacchetto offline dei modelli**: cartella con `<id>/<file>` o file sciolti; copia con verifica SHA-256 e dimensione come un download; i modelli con licenza da accettare si installano solo se la licenza è spuntata (nel primo avvio o nelle Impostazioni); il pacchetto GPU solo con le licenze NVIDIA accettate e solo se gli archivi ci sono tutti (mai un download nascosto).
- **File con estensione da foto ma contenuto non riconosciuto**: nell'analisi d'archivio restano nel job e finiscono nel report come danneggiati (prima venivano ignorati in silenzio).
- **Libreria che sparisce durante l'analisi**: pausa "sorgente non raggiungibile", non errori sui file.
- **Lingua** applicata al riavvio (P18), **tema** subito, **exiftool alternativo** verificato con `-ver` e attivo dal prossimo avvio, **template** con esempio e validazione, usato dall'import.
- **Benchmark in-app** (5f) rinviato alla fase 2, quando la tabella può comprendere tutte le fasi d'analisi; cifratura delle impronte (fase 2), esporta/importa (fase 5) e culling "normale" (fase 2, dopo le metriche del set etichettato) visibili e disattivati (F1-6).
- Corretto un difetto emerso dai test: gli aggiornamenti di Home, Job e Librerie sollevati dagli eventi dei job potevano sovrapporsi e duplicare le righe; ora leggono prima il catalogo e poi sostituiscono le liste.

## Da verificare a mano (per te)

1. **Subset d'archivio**: copia 1.000-2.000 foto in `E:\AI\PhotoFlowTestData\archive` (non c'è ancora); poi `dotnet test tests/PhotoFlow.Tests -c Release --filter "FullyQualifiedName~ArchivePerformanceTests"` scrive il report in `PhotoFlowTestData\lab\performance`.
2. **Librerie**: Librerie → "Aggiungi una cartella" su un tuo archivio → politica "Sempre sidecar" → "Avvia analisi": il job compare in Job come "In background"; avviando un import, l'analisi attende e poi riprende. Nell'archivio non deve comparire nessun file nuovo finché non scrivi XMP.
3. **Conferma D21**: scegliendo "Incorporati nei JPG" compare il numero di file non RAW coinvolti; "Annulla" lascia la politica com'era.
4. **NAS**: con una libreria su NAS, scollega la rete durante l'analisi → il job va in pausa ("sorgente non raggiungibile") e riprende con "Riprendi".
5. **Impostazioni**: Ctrl , e Ctrl Tab tra le schede; tema chiaro/scuro subito; lingua dopo il riavvio; "Backup ora"; "Ripristina…" su un backup e riavvio (il catalogo precedente resta in `backup\replaced-*`).
6. **Pacchetto offline**: su un PC senza rete, copia la cartella dei modelli (`%LOCALAPPDATA%\PhotoFlow\models\<id>\…`) e gli archivi NVIDIA, poi "Importa pacchetto offline…". **Le licenze (buffalo\_l, NVIDIA) le accetti tu**: PhotoFlow non le accetta mai al posto tuo.
7. Restano validi i punti manuali della fase 1 (XMP in Esplora risorse e Photoshop/Camera Raw, scheda SD vera, NEF Z5 II, set etichettato `quality\{buone,mosse,occhi_chiusi}`).

## Limiti noti e rinvii

HEIC tramite Magick.NET (libheif/libde265 in Magick.Native, LGPL statiche, brevetti HEVC: R19) da rivedere prima della prima versione distribuita. Unione dei sidecar negli incorporati quando cambia la politica di una libreria: fase 5 (sincronizzazione). Scritture del catalogo per foto in un'unica transazione: backlog. Il subset d'archivio reale non è ancora stato misurato.
