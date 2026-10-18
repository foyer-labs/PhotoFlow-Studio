# Piano della fase 2 — Raffiche, Revisione completa, Persone

Stato: **completata** il 2026-10-13 in autonomia (indicazione dell'utente del 2026-10-12: procedere fino alla fine del progetto senza fermarsi ai cancelli di fase, decidendo secondo la specifica); report in [`docs/reports/fase-2.md`](../reports/fase-2.md). Branch: `phase/2-review-people`.

Riferimenti: spec §3 (pipeline: fasi 3-5 e 7, `IBatchStage`), §5 (CLIP), §6 (raffiche, sequenze speciali, decisione per gruppo, metriche, correzioni, scelta del profilo), §7 (volti, impronte, clustering, cifratura), §9 (tabelle `Embeddings`, `Groups`, `GroupMembers`, `Persons`, `PersonPrototypes`, `Corrections`), §12bis (Revisione 2a-2h, Persone 4a-4c3, scorciatoie), §13 (fase 2), §14 (parità embedding ≥ 0,999, etichettatura); D8, D11, D12, D20, D23; P1, P2, P3, P4, P6, P12, P13, P17; R11, R18.

## Contenuto (spec §13)

Embedding CLIP; raffiche e sequenze speciali; schermata Revisione completa; riconoscimento volti, clustering, schermata Persone; cifratura impronte; modalità etichettatura. In più ciò che le fasi precedenti hanno rinviato alla 2: sezione Profili (placeholder "fase 2"), benchmark in-app (5f), modalità normale del culling quando le metriche lo consentono (D23), scelta automatica del profilo per gruppo (D8, richiede CLIP).

**Fuori dalla fase 2** (non anticipare): tag A+B, vocabolario, ricerca, duplicati (fase 3); OCR e vista Numeri (fase 4, presente ma disattivata); rigenerazione completa, esportazione, sincronizzazione XMP esterna (fase 5).

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13) | Evidenza prevista |
|---|---|
| Bracketing e timelapse mai classificati come raffiche nel set di test | Test `Unit` sulle regole e `Integration` su sequenze sintetiche con EXIF reali scritti da exiftool (bracketing ±EV a 0,2 s, timelapse a intervallo regolare, raffiche vere, cambi di soggetto, foto senza EXIF): nessun bracketing o timelapse in un gruppo "raffica"; misura anche sul set locale |
| Revisione interamente utilizzabile da tastiera | Test headless che svolge una revisione completa solo con tasti (navigazione, P/X/stelle, confronto, zoom, sincronizzazione, cambio profilo, filtri, conferma e scrittura); test che ogni comando della Revisione ha una scorciatoia nella tabella dei comandi |
| Chiave delle impronte rimossa: ricalcolo riuscito con assegnazioni preservate | Test `Integration`: catalogo con persone e volti assegnati a mano, chiave eliminata → riconoscimento sospeso (P13), ricalcolo delle impronte dalle anteprime → stesse assegnazioni, riconoscimento di nuovo attivo; con password: sblocco senza ricalcolo |
| Metriche del culling calcolate dal set etichettato con margine di incertezza | Modalità etichettatura con pannello metriche (intervalli di Wilson al 95%); test `Unit` sui conteggi e sugli intervalli; `CullingQualityTests` esteso alle tre metriche con intervallo; la misura sul set reale resta in attesa del set etichettato dell'utente (D20) |

## Tappe

| Tappa | Contenuto | Progetti |
|---|---|---|
| 2.0 | Test prima del codice: I5 per gruppi, volti e persone (decisioni e assegnazioni manuali mai sovrascritte da raggruppamento, riconoscimento o clustering); impronte mai in chiaro nel catalogo né nei log; chiave rimossa → ricalcolo con assegnazioni preservate; sequenze speciali mai raffiche | Tests |
| 2.1 | CLIP ViT-B/32 fp32 (F2-1): adattatore immagine (ridimensionamento bicubico Pillow del lato corto a 224, ritaglio centrale, normalizzazione) e testo con tokenizzatore BPE in C#; parità contro l'originale PyTorch (`tools/lab/clip_refs.py`); tabella `Embeddings`; embedding calcolato nella fase 3 della pipeline | Tagging, Data, Analysis |
| 2.2 | Raggruppamenti (barriera di lotto, `IBatchStage`): raffiche (≤ 2,0 s da `SubSecDateTimeOriginal` e similarità ≥ 0,90; senza EXIF solo similarità; crollo della similarità spezza), bracketing (compensazione variabile a scatti ravvicinati), timelapse (intervallo regolare ripetuto), panorama solo manuale; tabelle `Groups`/`GroupMembers`; ripresa idempotente | Analysis, Data, Sources |
| 2.3 | Decisione per gruppo: punteggio composito con i pesi del profilo normalizzato nella raffica, migliore / simili / pari merito entro 0,02 → da rivedere; scarti tecnici invariati; I5. Scelta automatica del profilo per gruppo con CLIP zero-shot e riserva (D8); "Rianalizza come…" per gruppo | Analysis |
| 2.4 | Impronte dei volti: ArcFace (w600k\_r50 di buffalo\_l) e SFace (alternativa YuNet), ritaglio allineato sui 5 punti con la preparazione antialias già usata per 2d106det; parità con insightface/OpenCV | Faces |
| 2.5 | Cifratura delle impronte (D12): AES-GCM per vettore, chiave dati protetta da DPAPI dell'account Windows o da password (PBKDF2); chiave non disponibile → galleria consultabile, riconoscimento sospeso, password o ricalcolo (P13) | Faces, Data, App |
| 2.6 | Riconoscimento e clustering: confronto con i campioni per anno delle persone note (P12), HDBSCAN su grafo k-NN in C# (R18) a blocchi per anno, validato contro l'HDBSCAN di Python; soglie che preferiscono dividere; "Persona N" | Faces |
| 2.7 | Revisione completa (2a-2h): griglia per raffiche con intestazioni, filtri, taglie S/M/L; Confronto 2-4 con zoom sincronizzato (PanAndZoom); semantica di P (P1); decisioni durante l'analisi (P2, 2e); cambio profilo per gruppo; conferma con riepilogo e passaggio a Persone (2g); errore parziale con "Riprova" e "Solo catalogo per questi" (2h, P4); "Sposta scarti…" su richiesta (P6); vista Numeri visibile e disattivata (fase 4) | App |
| 2.8 | Persone (4a-4c3): gruppi per dimensione, rinomina con suggerimenti, unisci, separa, ignora, pagina persona (volti per anno, prima i meno simili, "non è lei/lui", copertina, elimina persona con le impronte); XMP: regioni MWG con nome e `Persone\|Nome` solo per foto confermate | App, Metadata |
| 2.9 | Etichettatura (2d): sessioni da ~100, alla cieca, 1-5/Spazio, raffiche con la migliore, selezione delle foto più utili, metriche con intervallo; tabella delle etichette; `Corrections` registrate a ogni correzione con i segnali dell'epoca; modalità normale sbloccata solo con le tre metriche entro obiettivo (D23) | App, Analysis, Data |
| 2.10 | Profili: elenco, duplica, modifica pesi e soglie (P17), predefiniti non modificabili, personalizzati in JSON versionati nella cartella di configurazione | App, Analysis |
| 2.11 | Benchmark in-app (5f): ~50 foto della libreria, ms/foto e memoria per fase, stima import e archivio | App, Inference |
| 2.12 | Misure e report: sequenze speciali, revisione da tastiera, chiave rimossa, metriche; prestazioni con CLIP e impronte | Tests |

## Decisioni tecniche prese in autonomia (da registrare in `docs/progress.md` e nella specifica)

| # | Decisione | Motivo |
|---|---|---|
| F2-1 | CLIP fp32 su CPU e GPU; la conversione quantizzata esce dal manifest | Laboratorio 13 ott 2026 (40 foto reali): fp32 coseno 1,000000 con l'originale su CPU e CUDA (anche il testo); quantizzata 0,87-0,97, sotto la soglia di parità 0,999 e senza accelerazione su GPU (§5) |
| F2-2 | Clustering HDBSCAN scritto in C# su grafo k-NN (distanza di raggiungibilità reciproca, albero di copertura minimo, albero condensato, selezione per stabilità) al posto di HdbscanSharp | R18: HdbscanSharp è O(n²) e inattivo; validazione contro l'HDBSCAN di scikit-learn in `tools/lab` |
| F2-6 | Selezione dei cluster "excess of mass" con `min_cluster_size` 3 e `min_samples` 2 (non "leaf") | Laboratorio 13 ott 2026: altezze del dendrogramma identiche a scikit-learn; con min_samples 2 partizioni identiche; "leaf" è instabile sui pari merito (Rand 0,89-0,95 tra implementazioni corrette) mentre EOM con min_samples 2 divide già finemente (22 gruppi su 200 volti di laboratorio) |
| F2-3 | Chiave delle impronte: DPAPI (account Windows) per default su Windows; altrove password (il portachiavi Mac/Linux va nel backlog) | D12 con Windows prioritario; i test su Linux usano la password |
| F2-4 | I raggruppamenti si ricalcolano per intero quando il lotto del job è completo (idempotenti); le decisioni dell'utente restano (I5) | Ripresa semplice e sicura; costo di secondi |
| F2-5 | "Sposta scarti…" solo dentro la stessa libreria (`_scarti/` con lo stesso percorso relativo), quindi rinomina sullo stesso volume, sidecar insieme alla foto, catalogo aggiornato; mai copia + cancellazione | I4: nessuna cancellazione di file dell'utente |

## Rischi

| Rischio | Mitigazione |
|---|---|
| Ambito ampio (R1) | Tappe con test e commit propri; ciò che non serve ai criteri va nel backlog |
| Errori del riconoscimento (R11) | Soglie che preferiscono dividere, revisione nella pagina persona, campioni per anno |
| Allineamento dei volti diverso da insightface | Stessa preparazione dell'ingresso per C# e riferimento; confronto informativo con `norm_crop` |
| PanAndZoom / ItemsRepeater con Avalonia 12 | Verifica della versione all'inizio della 2.7; alternativa: zoom con `MatrixTransform` condiviso |
| Prestazioni: CLIP + impronte per foto | Misura nella 2.12; CLIP sull'anteprima già decodificata |

## Dati

Riferimenti di parità CLIP già generati (`PHOTOFLOW_TESTDATA/parity/clip`, 40 foto); riferimenti delle impronte e del clustering da generare con `tools/lab` sulle foto locali (mai nel repository). Sequenze sintetiche per raffiche/bracketing/timelapse generate dai test. Set etichettato: in attesa dell'utente.
