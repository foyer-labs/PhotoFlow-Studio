# Report di fine fase 1 — Import, analisi base, XMP, catalogo

Data: 2026-10-12 · Branch: `phase/1-import` · Piano: [`docs/plans/fase-1.md`](../plans/fase-1.md)

## Checklist dei criteri di accettazione (§13)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Import di 1.000 NEF: zero file persi, tutti verificati | ✅ Superato | `ImportScenarioTests.A_thousand_files_are_all_copied_verified_and_analyzed` (CI Windows + Linux): 1.000 file con firma NEF, copie identiche per SHA-256, 1.000 analisi, scheda invariata. `ImportPerformanceTests` (locale) sul set reale: 241 file (178 NEF, 52 JPG, 11 XMP), 0 errori, tutti `Done`, sorgente invariata. NEF reali disponibili: 178 (non 1.000) |
| 2 | Secondo import della stessa scheda: nessun duplicato | ✅ Superato | `Importing_the_same_card_twice_creates_no_duplicates`: al secondo inserimento 0 nuove / 20 già importate; forzando "Tutte" (P9) nessuna seconda copia e nessun secondo asset |
| 3 | Chiusura forzata a metà: ripresa dal punto di interruzione | ✅ Superato | `An_import_killed_midway_resumes_without_losing_or_redoing_photos`: il processo `PhotoFlow.ScenarioHost` viene terminato (intero albero) dopo ≥ 15 foto completate; al riavvio il job è rilevato come interrotto, riprende e termina; nessuna foto completata prima dell'interruzione rielaborata, tutte le copie identiche, nessun `.pftmp` residuo. Verde in CI su Windows e Linux |
| 4 | SD in sola lettura: nessun tentativo di scrittura o cancellazione | ✅ Superato | `IngestInvariantTests` (I1): file system registrante che fallisce a qualunque scrittura sotto la sorgente, impronte prima/dopo; `NoDeleteInProductCodeTests` (I4); impronte della scheda prima/dopo anche nei test del job, degli scenari e delle prestazioni |
| 5 | RAW invariati dopo la scrittura XMP | ✅ Superato | `RawIsNeverModifiedTests` (I2) con exiftool su NEF CC0 di raw.pixls.us: D40 con le tre politiche, D5100 e Z8 HE con la politica predefinita; impronta identica, solo sidecar |
| 6 | XMP riletto correttamente da exiftool; checklist manuale in Esplora risorse e Photoshop/Camera Raw | ✅ Automatico · ⏳ Manuale | `XmpWriterTests`: scrittura → rilettura per le tre politiche, unione delle keyword senza sovrascrittura, `_original` conservato. `PageFlowTests`: dalla Revisione, conferma e scrittura reale (sidecar per il NEF). **La checklist manuale è per te** (sotto) |
| 7 | Set di foto mosse/occhi chiusi: percentuale di intercettazione misurata | ⏳ In attesa del set | `CullingQualityTests` pronto: legge `PHOTOFLOW_TESTDATA/quality/{buone,mosse,occhi_chiusi}` e produce report con intervallo di Wilson al 95% e i target di §6. Il set etichettato non c'è ancora (F1-7). Misura sostitutiva in laboratorio (sotto) |

## Cosa è stato fatto (tappe 1.0-1.10)

| Tappa | Contenuto |
|---|---|
| 1.0 | Test delle invarianti I1-I5 scritti per primi; `IFileSystem` senza cancellazione; file system registranti e con guasti iniettati |
| 1.1 | Catalogo SQLite: migrazioni 0001-0003 solo in avanti con backup, lock esclusivo (con nuovi tentativi per 2 s), journal per percorsi di rete, store per librerie, asset, job, decisioni (I5), XMP, copie differite, analisi |
| 1.2 | exiftool 13.59 `-stay_open` con riavvio; lettura EXIF (incluso punto AF in coordinate); XMP con le tre politiche, unione e verifica |
| 1.3-1.4 | Rilevamento schede, scansione per firma, coppie RAW+JPG; copia a lettura singola con xxHash3, `.pftmp` → verifica → rinomina, mai sovrascritture; destinazioni locali e NAS differita con ritentativi; template percorsi; già importate |
| 1.5 | Gestori RAW (anteprima incorporata, ripiego LibRaw via Magick.NET) e JPEG; cache miniature |
| 1.6 | ONNX Runtime 1.31: CUDA (TF32 disattivato) + CPU; pacchetto GPU NVIDIA scaricato con licenze accettate; registro modelli con SHA-256 e accettazione licenze |
| 1.7 | Analisi: nitidezza (blur effect multi-scala, Laplaciano per le raffiche), esposizione, soggetto (D-FINE), volti e occhi (buffalo\_l; YuNet senza occhi), decisione prudente, 7 profili in `profiles/*.json` |
| 1.8 | Job di import con canali limitati, `JobItems`, pausa che conserva il lavoro fatto, ripresa, rilevamento della chiusura imprevista, ritentativo dei file falliti |
| 1.9 | Interfaccia: Home (3a-3c), procedura di import (3d-3f), Job (3g-3i), Revisione (2a in sottoinsieme, 2g, 2h); sezioni future presenti ma disattivate; IT/EN; scorciatoie per vista |
| 1.10 | Test locali di qualità e prestazioni; parallelismo dell'analisi tarato |

Test: 289, di cui 22 locali (parità, qualità, prestazioni) saltati senza `PHOTOFLOW_TESTDATA`; gli altri girano in CI, verde su Windows e Linux.

## Parità con i riferimenti (§14)

| Componente | Riferimento | Risultato |
|---|---|---|
| D-FINE S/M (CPU e CUDA) | PyTorch originale | Rilevamenti tutti abbinati, IoU ≥ 0,9998; tensori entro T1 |
| buffalo\_l SCRFD + 2d106det | insightface 2.1 | Ingressi identici bit per bit; CPU Δ = 0; CUDA Δ punteggi 5e-7, landmark 4e-4 del volto (265 volti) |
| YuNet | OpenCV 5.0 `FaceDetectorYN` | 247 volti, IoU ≥ 0,99999, Δ punteggi ≤ 6e-7 |
| Segnali immagine (grigio, sfocatura 3×3, Laplaciano) | OpenCV 5.0 | Identici bit per bit (grigio su tutti i 16,7 M valori RGB); varianze a 6 decimali su 40 foto |
| Blur effect (singolo e multi-scala) | scikit-image | 12 decimali sui dati sintetici, 9 su 40 foto |
| Ricampionamento (anche con riquadro frazionario) | Pillow 12.3 | Identico bit per bit |

## Misure

**Prestazioni** (`ImportPerformanceTests`, Release, 230 foto reali, 24 thread + RTX 3090, CUDA): copia verificata + anteprima + miniatura + D-FINE M + volti/occhi + decisione: **10,1-10,9 foto/s, 1.000 foto in ~1,6 min**. Il collo di bottiglia è la CPU (anteprime e segnali): con 2 analisi in parallelo 4,8 foto/s, 4 → 7,4, 8 → 10,1-10,9, 12 → 9,1. Predefinito: un terzo dei thread, da 2 a 8.

**Nitidezza** (`tools/lab/blur_metrics.py`, 230 foto reali sfocate artificialmente): con la soglia sulla peggiore originale la varianza del Laplaciano intercetta il 5-30% del mosso (9-25 px), il *blur effect* il 61-90% → misura adottata per lo scarto assoluto. Soglie iniziali: scarto sotto 0,35 (nessuna originale; 74% delle sfocature σ 2,5; 64-87% del mosso 15-100 px), revisione sotto 0,45.

**Esito sulle 230 foto reali** (profilo Eventi): nessuno scarto, 7,4% da rivedere (target < 20%). Le foto da rivedere sono volti mossi o in ombra, alte luci sui volti, un occhio chiuso: verificate a vista.

**Occhi**: contorni 33-42 e 87-96 dei 106 landmark. Dalle foto reali: occhi "bassi" anche per volti mossi, rivolti in basso, di profilo, con occhiali da sole → sconosciuto in questi casi (profilo, sguardo in basso, occhiali); scarto solo con entrambi gli occhi chiusi su un volto principale grande. Soglie da tarare sul set etichettato.

## Decisioni tecniche prese durante la fase

Registrate in `docs/progress.md` e nella specifica (§3, §4, §6, §9, §12bis, §14, §15, §16, §17): analisi in C# con parità OpenCV/scikit-image (F1-4); nitidezza assoluta con *blur effect* multi-scala e scarto solo se zona **e** fotogramma sono sfocati (lo sfondo sfocato voluto non basta); esposizione solo nella zona del soggetto; volti con preparazione antialias; punto AF dalle coordinate Nikon (mirrorless e live view); job nel progetto Sources; unità di pipeline = foto; pausa che conserva il lavoro fatto; parallelismo automatico; lock con nuovi tentativi; mappa XMP di fase 1; scorciatoie legate alla vista.

## Da verificare a mano (per te)

1. **Avvio e primo avvio**: avvia l'app (`dotnet run --project src/PhotoFlow.App -c Release`). Nel primo avvio: hardware (il pacchetto GPU su questo PC è già installato), poi "Rivedi licenze e scarica": **l'accettazione della licenza di buffalo\_l spetta a te** (in alternativa YuNet, senza occhi); infine la prima libreria.
2. **Scheda vera**: inserisci una SD con foto → banner "Scheda rilevata" con nuove/già importate → procedura → job → Revisione.
3. **Revisione**: frecce, P, X, 1-5, 0, Ctrl+Z; filtri per esito; Ctrl+Invio → conferma → "Scrivi XMP".
4. **XMP in Esplora risorse**: su un JPG valutato con stelle, Proprietà → Dettagli mostra la valutazione.
5. **XMP in Photoshop/Camera Raw (o Bridge)**: JPG e NEF mostrano stelle ed etichetta rossa ("da rivedere" o scarto in modalità prudente); accanto al NEF c'è `nome.xmp`, il NEF non è modificato.
6. **Disco scollegato**: con una destinazione di backup su disco esterno, scollegalo durante la copia → il job va in pausa (3i) e riprende quando torna.
7. **Z5 II** (quando avrai i NEF, F1-1): anteprima incorporata, NEF HE, tag del punto AF (`AFAreaXPosition`…).
8. **Set etichettato** (F1-7): `E:\AI\PhotoFlowTestData\quality\buone` (≥ 100), `mosse` (30-50), `occhi_chiusi` (30-50); poi lancio `CullingQualityTests` e taro le soglie.

## Decisione richiesta: B1 — rotazione dei backup e invariante I4

I4 dice "Nessun file viene eliminato automaticamente". Alla lettera vale anche per i file interni di PhotoFlow: backup del catalogo prima di ogni migrazione (§9 chiede "N versioni"), cache delle miniature, temporanei, archivi scaricati del pacchetto GPU (~2,5 GB, oggi conservati come pacchetto offline). Oggi si accumulano.

| Opzione | Effetto |
|---|---|
| **a) Chiarire I4 (consigliata)** | I4 riguarda i file dell'utente (foto, RAW, sidecar, copie `_original`, sorgenti e destinazioni). I file interni di PhotoFlow nelle sue cartelle (`IAppPaths`: backup oltre gli ultimi N, cache, temporanei, download) possono essere ruotati o puliti, con regole scritte in §9 e test che vietano qualunque cancellazione fuori da quelle cartelle |
| b) Lasciare I4 alla lettera | Nessuna pulizia automatica; un comando esplicito in Impostazioni ("Libera spazio") mostra cosa verrebbe eliminato e chiede conferma |
| c) Altro | — |

## Limiti noti e rinvii

Impostazioni, Librerie e importazione offline dei modelli arrivano con la fase 1b; riconoscimento volti e profilo automatico con la fase 2; ricerca e duplicati con la fase 3 (sezioni presenti ma disattivate, F1-6). Licenze statiche LGPL in Magick.Native (R19) da rivedere prima della prima versione distribuita (`THIRD-PARTY-NOTICES.md`). Soglie del culling iniziali: la modalità prudente resta attiva (D20).
