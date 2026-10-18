# PhotoFlow — Registro di avanzamento

Memoria del progetto tra una sessione e l'altra. Aggiornato a fine sessione.

## Stato

| Voce | Valore |
|---|---|
| Fase corrente | 5b — Didascalie VLM in italiano (livello D, Qwen2.5-VL-7B): report in [docs/reports/fase-5b.md](reports/fase-5b.md). Fasi 0-5 completate e unite in `main` |
| Branch | `phase/5b-vlm` (PR verso `main`) |
| Remoto | https://github.com/foyer-labs/PhotoFlow.git (pubblico) |
| Autore dei commit | Foyer Labs <foyerlabs@gmail.com> (config locale del repository) |
| Ultimo aggiornamento | 2026-10-13 |

## Ambiente di sviluppo rilevato (2026-10-12)

| Strumento | Versione | Nota |
|---|---|---|
| .NET SDK | 10.0.401 (+ 9.0.318) | SDK 10 installato con winget il 2026-10-12 |
| Python | 3.12.9 | |
| uv | 0.11.28 | |
| git / gh | 2.51 / 2.101 | gh autenticato come foyer-labs |
| GPU | NVIDIA RTX 3090, 24 GB | Driver 610.88 (CUDA 12/13) |
| exiftool | 13.59 (solo laboratorio) | In `PHOTOFLOW_TESTDATA/tools/exiftool-13.59`, SHA-256 verificato |
| `PHOTOFLOW_TESTDATA` | `E:\AI\PhotoFlowTestData` | Variabile utente impostata il 2026-10-12 (i processi già aperti non la vedono finché non vengono riavviati) |

## Attività completate

### Sessione 1 — 2026-10-12
- Lettura di CLAUDE.md, docs/spec.md (intero), panoramica delle tavole in docs/design/.
- Creato questo registro.
- `git init` locale con identità Foyer Labs e remoto `origin`; nessun commit.
- Installato .NET SDK 10.0.401 (winget).
- Risolti con l'utente i punti A1-A11 (vedi decisioni); spec.md aggiornata in §2 (D21), §3, §4, §5, §6, §7, §8, §10 (I1), §13, §14, §17 (P11, backlog).
- Fase 0 sviluppata su phase/0-setup (7 commit): soluzione e Core, guscio Avalonia, CI, hook, manifest, laboratorio, parità D-FINE, benchmark, verifica RAM++, verifiche 0.6. Dettagli ed evidenze in [docs/reports/fase-0.md](reports/fase-0.md).
- Modelli scaricati e verificati in PHOTOFLOW_TESTDATA/models (buffalo_l con accettazione della licenza per uso di laboratorio).
- Immagini di sviluppo: 8 COCO val2017 in PHOTOFLOW_TESTDATA/photos-dev (solo per provare gli script).

## Decisioni prese con l'utente

| Data | Decisione |
|---|---|
| 2026-10-12 | Repository: github.com/foyer-labs/PhotoFlow; autore dei commit foyer-labs (Foyer Labs <foyerlabs@gmail.com>) |
| 2026-10-12 | A2 — .NET 10 LTS (`net10.0`), SDK fissato con `global.json` (`rollForward: latestFeature`) |
| 2026-10-12 | A3 — `IInferenceSession` + `NamedTensor` + `ExecutionTarget` in Core; solo `.Inference` dipende da ONNX Runtime; adattatori nei progetti di dominio con interfacce per funzione. spec.md §3 aggiornata |
| 2026-10-12 | A4 — D-FINE: manifest su `onnx-community/dfine_{s,m}_obj2coco-ONNX` (fp32), fissati per commit + SHA-256; `export_dfine.py` da `ustc-community` per la parità; riserva: nostro export su GitHub Releases (campo `mirrors`). spec.md §5 aggiornata |
| 2026-10-12 | A7 — Hook in `.githooks/pre-commit` (sh, senza dipendenze) attivato con `core.hooksPath`; stesso controllo in CI; rilevamento per estensione + firma binaria; ammessi `tests/data/public/` (ogni file in `LICENSES.md`) e i percorsi esatti in `.githooks/allowed-images.txt` (oggi solo l'icona dell'app); icone UI come geometrie XAML |
| 2026-10-12 | A8 — Parità dei rilevatori su due livelli (tensori con ingresso identico + rilevamenti abbinati per classe, IoU ≥ 0,95, \|Δ punteggio\| ≤ 0,02, tolleranza ±0,05 dalla soglia). spec.md §14 aggiornata |
| 2026-10-12 | A5 — Con YuNet/SFace gli occhi sono sempre "sconosciuto"; limite dichiarato nel primo avvio (P11). Nessun modello occhi aggiuntivo. spec.md §6, §7, P11 aggiornate. Nota: le fonti indicano YuNet MIT (non Apache 2.0): da confermare in 0.6 |
| 2026-10-12 | A6 — Librerie da archivio esistente: default `SidecarOnly`, passaggio a `EmbeddedForNonRaw` solo su conferma con numero di file e `_original`. D21, §4, I1 (§10) aggiornate; unione sidecar↔incorporati nel backlog (fase 5). CLAUDE.md I1 già coerente ("scheda SD o cartella sorgente") |
| 2026-10-12 | A9 — Ripresa: `JobItems` e ripresa dell'Import in fase 1 (Home 3c); job d'archivio in background, parallelo, pausa in 1b. spec.md §13 aggiornata. Da proporre nel piano fase 1: copia su `.pftmp` + rinomina dopo verifica, sovrascrittura del temporaneo alla ripresa (nessuna eliminazione, I4) |
| 2026-10-12 | A11 — `PROMPT_Claude_Code.md` resta solo locale: da inserire in `.gitignore` (fase 0.1), mai committato |
| 2026-10-12 | Piano fase 0 approvato (`docs/plans/fase-0.md`): Avalonia 12.1.4, xUnit v3 3.2.2 fissato, download del laboratorio (~7 GB) e push su GitHub autorizzati. spec §3 (+`IObjectDetector`, `IImageOperations`) e §15 (YuNet MIT) aggiornate |
| 2026-10-12 | `IInferenceProvider.CreateSessionAsync(model, target, ct)` asincrono (conformità a CLAUDE.md §4). spec §3 aggiornata |
| 2026-10-12 | Verifiche 0.6 applicate a spec.md: §15 licenze (Velopack, PanAndZoom, HdbscanSharp, RapidOcrNet, Magick.NET, DirectML/Windows ML, CLIP, RAM++), §16 rischi R15-R20, note di laboratorio in §5, §17 verifiche tecniche aggiornate. T1 (tolleranza parità tensori) rinviata: si decide dopo la misura sulle foto reali |
| 2026-10-12 | Nessun NEF della Z5 II disponibile fino a dopo l'implementazione: la "primissima attività della fase 1" (§13, verifica anteprima/HE/punto AF sui NEF Z5 II) va rinviata; proposta da fare nel piano della fase 1. Set di test reale: 240 file in `PHOTOFLOW_TESTDATA/photos` (178 NEF Nikon D40 lossy + D5100 lossless, 52 JPG di cui 49 Samsung SM-A500FU, 11 .xmp preesistenti, mai toccati) |
| 2026-10-12 | T1 — Parità livello tensori: query abbinate con assegnazione ottima prima del confronto; punteggi (sigmoide) ≤ 5e-3, riquadri ≤ 5e-4 (misurati 2,7e-3 e 1,5e-4 su 40 foto reali). spec.md §14 e `DFineParityTests` aggiornati |
| 2026-10-12 | Report fase 0 approvato; PR #1 unita in `main` |
| 2026-10-12 | F1-1..F1-7 — decisioni del piano della fase 1 (vedi `docs/plans/fase-1.md`): rinvio verifica Z5 II; CUDA + CPU (DirectML escluso per risultati errati); Magick.NET per RAW di riserva; analisi in C# con parità OpenCV; exiftool scaricato in build; layout completo con funzioni future disattivate; set qualità a cartelle. spec §3, §4, §5, §11, §13, §14, §15, §16, §17 e CLAUDE.md §3 aggiornati |
| 2026-10-12 | Autonomia: da qui l'utente delega le scelte tecniche (verificare alternative, scegliere la più robusta, misurare dove serve); si ferma solo per decisioni operative e per i cancelli di fase |
| 2026-10-12 | Tecnica (fase 1.0-1.2): `IFileSystem` senza cancellazione (I4 per costruzione); copia verificata `.pftmp` → verifica → rinomina, mai sovrascritture (nome libero `-2`); `IMetadataWriter` riceve `MetadataTarget` (spec §3); sidecar `nome.xmp` condiviso dalle coppie RAW+JPG; incorporamento XMP tramite sostituzione verificata (`ImageDataHash` invariato) con `_original` conservato per default; exiftool: distribuzione Perl dal tag GitHub dell'autore in CI/Linux/macOS, eseguibile Windows da SourceForge per le build locali (SourceForge rifiuta i runner CI); campioni RAW pubblici CC0 (raw.pixls.us) scaricati e verificati dai test, mai nel repository; JPG di test sintetici |
| 2026-10-12 | Tecnica (fase 1.1): catalogo SQLite con connessione unica serializzata, file di lock tenuto aperto e mai cancellato, enumerazioni come testo e istanti in millisecondi Unix (niente gestori globali Dapper), backup con la backup API prima di ogni migrazione |
| 2026-10-12 | L'utente accetta le licenze NVIDIA (CUDA Toolkit EULA, cuDNN SLA) per installare il pacchetto GPU su questo PC (sviluppo). Verifica licenze: ridistribuzione delle DLL di runtime permessa con l'app, solo su GPU NVIDIA, non modificate, LICENSE.txt conservati, accettazione richiesta all'utente nel primo avvio |
| 2026-10-12 | Tecnica (fase 1.6): CUDA con TF32 disattivato (con TF32 la parità D-FINE crolla: Δ punteggi 0,16, senza guadagno di velocità); pacchetto GPU da archivi NVIDIA ufficiali (CUDA 13.0.3 + cuDNN 9.27), DLL estratte per nome, archivi conservati come pacchetto offline |
| 2026-10-12 | Tecnica (fase 1.7): volti buffalo\_l con preparazione antialias e parità insightface 2.1 (CPU identica); punto AF dalle coordinate Nikon (`AFAreaXPosition`…, mirrorless e live view; punti nominali delle reflex senza posizione: zona successiva); nitidezza assoluta = 1 − blur effect multi-scala (il Laplaciano dipende dal contenuto: muri, notturne, panning), Laplaciano per la classifica in raffica; occhi sconosciuti di profilo o rivolti in basso; esposizione solo nella zona del soggetto; analisi a 2048 px. Dettagli e misure in spec §6 e §14 |
| 2026-10-12 | Tecnica (fase 1.8): job di import in `PhotoFlow.Sources` (QuickHash/xxHash non possono stare in Core); unità di pipeline = foto (gemelli RAW+JPEG), in analisi solo con tutti i file verificati (I3), analisi sul JPEG se presente; impostazioni del job salvate nel job (ripresa identica); in pausa il lavoro già calcolato viene registrato; un job "in esecuzione" all'apertura del catalogo (lock esclusivo) è un'interruzione → in pausa; un job completato con errori si può rilanciare per ritentare i soli file falliti |
| 2026-10-12 | Tecnica (fase 1.9-1.10): interfaccia con pagine per sezione e sezioni future visibili ma disattivate (F1-6); scorciatoie per vista legate alla vista; griglia di Revisione virtualizzata a righe; mappa XMP di fase 1 (spec §4); Ctrl I globale; lock del catalogo con nuovi tentativi per 2 s; analisi in parallelo automatica (un terzo dei thread, 2-8; misurato: 1.000 foto in ~1,6 min con copia verificata, D-FINE M e buffalo\_l su RTX 3090) |
| 2026-10-12 | Report della fase 1 approvato. B1: I4 chiarita — protegge i file dell'utente; i file interni di PhotoFlow nelle cartelle di `IAppPaths` (backup, cache, temporanei, download) si possono ruotare o pulire con regole in spec §10 e un test che vieta cancellazioni altrove. spec §10 e CLAUDE.md aggiornati |
| 2026-10-12 | PR della fase 1 unita in `main`. Fase 1b: subset d'archivio fornito dall'utente in `E:\AI\PhotoFlowTestData\archive` (solo lettura); piano in `docs/plans/fase-1b.md` da approvare |
| 2026-10-12 | L'utente, assente per ore, chiede di procedere in autonomia fino alla fine del progetto (fasi 1b-5) senza fermarsi ai cancelli di fase, decidendo secondo la specifica e quanto concordato. Piani e report restano scritti e le PR vengono unite dopo la CI verde; restano all'utente le accettazioni di licenza (buffalo\_l, NVIDIA), le verifiche manuali e i set etichettati |
| 2026-10-12 | A1 — Tavole rinominate come in §12bis (`PhotoFlow_*_dc.html`); specifica invariata |
| 2026-10-12 | A10 — Ricostruite le celle troncate di §8 (Volti: `Persone\|Nome`; OCR: `Motorsport\|Numero N`, `Motorsport\|Pilota\|Nome`, `Motorsport\|Team\|Nome`) e §17 (Album → D6), con pipe protetti |
| 2026-10-13 | Tecnica (fase 1b.6): sezione Librerie (cartella esistente → `SidecarOnly`, analisi in background a bassa priorità, passaggio a XMP incorporato solo dopo conferma con il numero di file non RAW, D21) e Impostazioni a schede (5f-5j). Decisioni prese in autonomia: ripristino del catalogo da backup **al prossimo avvio** (il catalogo è aperto durante la sessione; il catalogo sostituito si sposta in `backup/replaced-*`, mai cancellato; il backup scelto è escluso dalla rotazione; un backup danneggiato viene rifiutato senza toccare nulla); all'avvio con catalogo danneggiato una pagina propone il ripristino dell'ultimo backup (spec §9); pacchetto offline dei modelli = cartella con `<id>/<file>` o file sciolti, copia verificata SHA-256, licenze da accettare solo se già spuntate; lingua (applicata al riavvio, P18), tema (immediato), exiftool alternativo (verificato con `-ver`, attivo al prossimo avvio) e template dei percorsi nelle impostazioni; benchmark in-app rinviato alla fase 2 (tabella con tutte le fasi d'analisi); cifratura impronte (fase 2), esporta/importa (fase 5) e culling normale (fase 2) visibili e disattivati (F1-6) |
| 2026-10-13 | Tecnica (fase 1b.7): scenario di 10.000 file misti (JPEG, gemelli RAW+JPEG, PNG, TIFF, WebP, HEIC, 80 estensioni sbagliate, 30 danneggiati) con l'analisi reale senza modelli, processo terminato a 2.500 e ripreso: nessuna foto rifatta, danneggiati e solo loro nel report, cartella identica byte per byte (~4 min in locale, catalogo ~45 foto/s). Decisione presa in autonomia: nell'analisi d'archivio un file con estensione da foto ma contenuto non riconosciuto resta nel job e finisce nel report come danneggiato (prima veniva ignorato in silenzio). Misura locale in background (4 analisi in parallelo, CUDA, RTX 3090): 8,2 foto/s, stima 1,9 h per 57.130 foto; passata dell'hash completo ~510 MB/s. Il subset d'archivio in `PHOTOFLOW_TESTDATA/archive` non c'è ancora: la misura usa `photos` e si ripete da sola quando il subset viene copiato |

| 2026-10-13 | Fase 2 (tappe 2.0-2.12), decisioni prese in autonomia oltre a F2-1..F2-6 del piano: regole delle sequenze (bracketing ≥ 3 scatti su ≥ 0,3 EV di compensazione o ≥ 1 EV di EV100; timelapse ≥ 10 scatti a intervallo ≥ 1 s entro ±10%); soglie del riconoscimento (ArcFace 0,45, SFace 0,363, margine 0,05, fascia di suggerimento 0,10, 5 campioni per persona per anno, impronte da 48 px); regioni MWG scritte come struttura completa con exiftool conservando quelle di altri programmi, RAW con le persone del gemello; profili personalizzati come file versionati `custom-<nome>.v<n>.json` e profilo mancante sostituito da Eventi; rianalisi solo se cambia la chiave di misura del profilo, in parallelo (modalità automatica sul set locale da 95 s a 52 s); suggerimento della soglia di mosso da ≥ 30 correzioni (peso 3 agli scarti sbagliati); benchmark con VRAM da `nvidia-smi` e modelli che restano caricati; tasti di un carattere disattivati mentre si scrive in un campo. Scostamenti dalle tavole nel backlog (Ctrl Z e selezione a rettangolo in Persone, "Apri foto" dalla pagina persona, eliminazione dei profili personalizzati, caricamento dei modelli fase per fase). spec §5, §6, §7, §17 aggiornate |

| 2026-10-13 | Fase 2 unita in `main` (PR #4) dopo la CI verde |
| 2026-10-13 | Fase 3 (tappe 3.0-3.9), decisioni prese in autonomia: F3-1 RAM++ fp32 (`CannotFindObject/RAM_ONNX`, parità 52/52 con PyTorch, la int8 esce); F3-2 etichette italiane dei 4.585 tag preparate con un LLM durante lo sviluppo e incluse, tutte da rivedere (al posto dell'LLM locale della tavola 5c); F3-3 categorie predefinite e lista nera minima; F3-4 ricerca FTS5 + scansione esatta degli embedding, indice testuale aggiornato da trigger; F3-5 quasi-duplicati CLIP ≥ 0,95 + hash percettivo ≤ 10 bit; F3-6 copie esatte solo nella stessa libreria (tra librerie sono backup); keyword scritte da PhotoFlow ricordate in `XmpSync` per toglierle senza toccare quelle altrui; gerarchia in italiano una volta rivisto. Misure: ricerca su 60.000 foto < 0,27 s; analisi con RAM++ 4,15 foto/s. Scostamenti dalle tavole nel backlog (DataGrid del Vocabolario, zoom sincronizzato e "Mostra esclusi" in Duplicati, menu Evento e selezione multipla in Consultazione). spec §4, §5, §8, §10, §17 aggiornate |

| 2026-10-13 | Fase 3 unita in `main` (PR #5) dopo la CI verde |
| 2026-10-13 | Fase 4 (tappe 4.0-4.6), decisioni prese in autonomia: F4-1 modelli RapidOCR (PP-OCRv4 det, PP-OCRv3 rec inglese) con adattatori nostri e parità esatta; F4-2 cambiata: `.xlsx` letto con la libreria standard invece di `DocumentFormat.OpenXml`; F4-3 regole della scelta del numero; F4-4 riconoscitore OCR su CPU (9 volte più veloce che su GPU per i suoi ingressi); F4-5 cifra confusa con penalità ridotta se anche il numero letto è in elenco, e senza elenco solo numeri letti da soli. Elenco iscritti scelto nell'import o nella vista Numeri (la fase dei numeri si ripete), nomi dell'elenco cercabili (migrazione 0014). Corretti in passing: azioni rapide della Home ancora disattivate dalle fasi 2-3 e testi che citavano le fasi. Misura: OCR 97 ms per foto. Scostamenti dalla tavola 2c nel backlog. spec §5, §8, §9, §15, §17 aggiornate |

| 2026-10-13 | Fase 4 unita in `main` (PR #6) dopo la CI verde |
| 2026-10-13 | Fase 5 (tappe 5.0-5.8), decisioni prese in autonomia:<br>• F5-1 didascalie Florence-2 base-ft con adattatori nostri; l'encoder visivo di `onnx-community` non corrisponde all'originale, lo esportiamo noi; parità esatta su CPU e CUDA.<br>• F5-2 livello D rinviato: licenze del 3B e runtime separato del 7B.<br>• F5-3 trasferimento delle assegnazioni dei volti per IoU ≥ 0,5.<br>• F5-4 `.pfcatalog` con impronte ricifrate da una password d'esportazione; importazione al prossimo avvio.<br>• F5-5 sincronizzazione XMP con stato e contenuto del file.<br>Inoltre: scelta dei modelli (rilevatore, volti) tra quelli installati; pulsante di ricalcolo delle impronte quando un catalogo arriva senza. Misure: cambio di modello dei volti su 230 foto senza persone perse; didascalie +644 ms per foto. spec §4, §5, §8, §9, §15, §17 aggiornate. **Da fare all'utente**: pubblicare la release `models-florence2-base-ft-1` con `vision_encoder_pf.onnx` (azione pubblica, non fatta in autonomia) |
| 2026-10-13 | Fase 5b (riapertura di F5-2 da parte dell'utente): U1 Qwen2.5-VL-7B; U2 pesi scaricati a mano e convertiti dall'app; U3 Python già installato; U4 ONNX Runtime GenAI in un processo separato (eccezione a CLAUDE.md §3); U5 solo italiano, C resta. Durante la fase l'utente ha indicato di non forzare la VRAM: **P16 rivista a GPU ≥ 12 GB** (misura: picco ~10 GB del solo 7B int4) |
| 2026-10-13 | Fase 5b e posizione del catalogo scelta dall'utente (al primo avvio; Sposta / Apri un catalogo esistente) unite in `main` (PR #8) |
| 2026-10-13 | Richieste dell'utente dopo le prove (PR #9): pannello Filtri in Consultazione (persone, eventi, tag in gruppi AND/OR con conteggi, migrazione 0018); chiusura dell'anteprima grande; clic sulla copia da tenere nei Duplicati; Persone più chiare, «Sposta a…» con elenco, rinomina corretta, «Apri foto» con percorso, tag e riquadro; volti sulla foto con nomi, clic per dire chi è, volto aggiunto a mano (N). Corretta una gara tra salvataggi delle impostazioni vista in CI. spec §7, §12bis, §17 aggiornate |
| 2026-10-13 | Release **0.1.0** (branch `release/0.1.0`): installer e aggiornamenti Velopack (id `FoyerLabs.PhotoFlow`, feed dalle release GitHub, spec §11); disinstallazione che toglie solo cache e temporanei; README, guida utente, FAQ e note di rilascio in italiano con 13 screenshot da un catalogo dimostrativo disegnato; licenze nel pacchetto e revisione R19. Report in `docs/reports/release-0.1.0.md` |
| 2026-10-14 | NEF della Z5 II forniti dall'utente (High Efficiency*): chiuse le verifiche §13 (anteprima 6048×4032 a piena risoluzione, punto AF leggibile, HE). Trovato che LibRaw decodifica i NEF HE in un'immagine sbagliata senza errore: i NEF HE non le arrivano più. Il formato resta NEF anche se exiftool li chiama NRW. Analisi completa con i modelli dell'utente su copie dei file: tutto elaborato, didascalie e tag corretti. Test locale in `PHOTOFLOW_TESTDATA/z5ii` |
| 2026-10-15 | Livello D: **Qwen3.5-9B** (U1 rivista su richiesta dell'utente) accanto a Qwen2.5-VL, che resta dove già convertito finché l'utente non sceglie il nuovo. Ricetta Olive ufficiale corretta in 5 punti (MTP, exclude_embeds, fine risposta, limite immagine, pesi mancanti); tabella di embedding int8 (picco GPU 9,8 GB, P16 confermata); penalità di ripetizione 1,05. Pesi scaricati e convertiti sul PC dell'utente. Report in `docs/reports/qwen35.md` |
| 2026-10-15 | Persone (P22, su richiesta dell'utente): modello di riconoscimento scelto a parte dal rilevatore (ArcFace, SFace, **AdaFace IR-101** opzionale, non commerciale, scaricato dall'app dopo l'accettazione e convertito sul PC); rigenerazione su **tutto il catalogo**; **«Riparti da zero con le persone»** (persone, nomi, assegnazioni anche manuali, volti disegnati, dopo conferma con i conteggi e backup del catalogo; XMP riscritti). Misure: errori su LFW da 1,65% a 0,80% con volti di 16 px; soglia 0,42 a parità di falsi riconoscimenti; parità con la rete degli autori 0,99999. Report in `docs/reports/adaface.md` |

## Problemi aperti

| # | Tema | Sintesi |
|---|---|---|
| N5-N6 | Modelli quantizzati | RAM++ int8/CLIP quantizzato non accelerano su GPU; RAM++ int8 non supera la parità (52 JPG reali: 12/52 insiemi identici, Jaccard 0,866) — fase 3 |
| N1-N4 | Note per fasi successive | CLIP scheda modello (fase 2), DirectML (0.6→R3), GridSample fp16 CUDA (fase 1), YuNet ingresso fisso (fase 1) |
| F2-auto | Costo della modalità automatica | +55% sul set locale per la rianalisi dei gruppi che cambiano profilo: classificare per foto prima dell'analisi (backlog §17) |

## Verifiche manuali in attesa (per l'utente)

0. Preparare entro la tappa 1.10 il set etichettato: `E:\AI\PhotoFlowTestData\quality\buone` (≥ 100), `mosse` (30-50), `occhi_chiusi` (30-50).

1. ~~Avviare l'app~~ — fatto dall'utente il 2026-10-12: si apre e mostra la navigazione (comportamento atteso per la fase 0). Da controllare ancora, se possibile: pulsanti di sistema, trascinamento e doppio clic sulla barra del titolo, `-- --theme light`.
2. ~~Foto di test~~ — fornite il 2026-10-12 (240 file).

## Prossimo passo

PhotoFlow 0.1.0 pubblicato. Restano le verifiche manuali del report di rilascio (installazione, aggiornamento, disinstallazione) e di quelli delle fasi, e il backlog (§17).
