# Piano della fase 3 — Tag, vocabolario, Consultazione, Duplicati

Stato: **completata** il 2026-10-13 in autonomia (indicazione dell'utente del 2026-10-12); report in [`docs/reports/fase-3.md`](../reports/fase-3.md). Branch: `phase/3-tags-search`.

Riferimenti: spec §3 (`ISearchIndex`, pipeline fase 6 "tagging"), §4 (campi XMP: keyword piatte e gerarchiche), §5 (RAM++, livelli A-D, selezione hardware), §8 (fonti, vocabolario, traduzione, soglie, provenienza), §9 (tabelle `Tags`, `TagLabels`, `AssetTags`, `CustomTags`; FTS), §10 (prestazioni: ricerca), §12bis (Consultazione 4d-4e2, Duplicati 4f-4g, Vocabolario 5c-5d2, scorciatoie), §13 (fase 3), §14; D5, D6, D9; P5, P14, P15, P16.

## Contenuto (spec §13)

Tagging A (CLIP zero-shot sul vocabolario personale) e B (RAM++); vocabolario e traduzioni; Consultazione con ricerca testuale (FTS) e in linguaggio naturale; Duplicati. In più: l'import abilita le caselle dei tag A/B (oggi disattivate "fase 3"); la prima analisi di una libreria comprende i tag.

**Fuori dalla fase 3** (non anticipare): OCR, elenco iscritti, vista Numeri (fase 4); didascalie C/D, rigenerazione completa con anteprima differenze, esportazione/importazione, sincronizzazione XMP esterna (fase 5).

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13) | Evidenza prevista |
|---|---|
| Ricerca < 1 s su 60.000 foto | Test con un catalogo sintetico di 60.000 foto (tag IT/EN, persone, eventi, EXIF, embedding 512-d): ricerche testuali, per filtri esatti, in linguaggio naturale (ordinamento per significato) e miste, tempo misurato dalla richiesta al risultato; in CI con soglia larga, in locale (`Performance`) con la soglia di 1 s |
| Keyword IT+EN e gerarchiche lette da Bridge | Test `Integration`: XMP scritto e riletto con exiftool (`dc:subject` con etichetta italiana rivista + inglese, `lr:hierarchicalSubject` `Categoria\|Tag`, tag non rivisti solo in inglese, lista nera mai scritta); verifica in Bridge **a mano** (istruzioni nel report) |
| Rianalisi: tag manuali e soppressi preservati | Test delle invarianti scritti prima del codice (I5): un tag manuale non viene mai rimosso dalla rianalisi; un tag automatico rimosso dall'utente diventa "soppresso" e nessuna rianalisi lo reinserisce, con modelli o soglie diverse; vale anche per i tag CLIP personalizzati e per l'evento |

## Tappe

| Tappa | Contenuto | Progetti |
|---|---|---|
| 3.0 | Test prima del codice: I5 per i tag (manuali, soppressi, rianalisi con altro modello o soglie); lista nera mai scritta; nessun tag non rivisto scritto in italiano | Tests |
| 3.1 | RAM++ fp32 (F3-1): adattatore (384×384 bicubico, normalizzazione ImageNet, sigmoide), soglie ufficiali per tag + regolatore globale, tag da escludere dell'originale; parità contro i riferimenti PyTorch di `tools/lab` (insiemi identici) | Tagging, Inference |
| 3.2 | Catalogo dei tag: migrazione `0008_tags` (`Tags`, `TagLabels`, `AssetTags`, `CustomTags`, lista nera, categorie), vocabolario RAM++ con etichette italiane e categorie incluso nell'applicazione (F3-2, F3-3) | Data, Core |
| 3.3 | Fase di tagging nella pipeline: RAM++ sull'anteprima già decodificata (B), CLIP zero-shot sull'embedding già salvato (A, nessuna inferenza in più), evento del job come tag; rianalisi che rispetta I5; livelli scelti nell'import e predefiniti per hardware (sola CPU: solo A, §5) | Tagging, Analysis, Sources |
| 3.4 | XMP: `dc:subject` (inglese sempre, italiano se rivisto), `lr:hierarchicalSubject` `Categoria\|Tag`, evento, persone come già oggi; lista nera esclusa; firma XMP aggiornata | App, Metadata |
| 3.5 | Indice di ricerca (`ISearchIndex`, F3-4): FTS5 di SQLite su tag IT/EN, persone, evento, nome file e cartella; filtri esatti (persona, tag, data, evento, EXIF, rating, esito); ricerca vettoriale in memoria sugli embedding CLIP (prodotto scalare SIMD); testo libero → embedding testuale CLIP → ordinamento per significato (P14); "Trova simili" | Data, Core, Tagging |
| 3.6 | Consultazione (4d, 4e, 4e2): campo a gettoni + linguaggio naturale, filtri, griglia, dettaglio con tag modificabili (T), volti evidenziati, stelle, "Apri in Photoshop" (Ctrl E, percorso configurato), app predefinita (Ctrl ↵), Esplora risorse (Ctrl Maiusc E), Trova simili (S); nessun risultato con proposte; indice in costruzione | App |
| 3.7 | Duplicati (4f, 4g, F3-5): esatti (stesso hash completo) e quasi-duplicati (CLIP + hash percettivo), gemelli RAW+JPEG esclusi, proposta di quale tenere (originale, RAW, risoluzione, tag manuali), confronto affiancato con zoom sincronizzato, spostamento in `_duplicati` nella stessa libreria (P5, mai cancellazioni), gruppi "non duplicati" ricordati | Analysis, Data, App |
| 3.8 | Vocabolario (5c, 5d, 5d2): nucleo dei 500 tag più frequenti dopo 1.000 foto con tag (P15), revisione a righe da tastiera (Invio approva, F2 modifica, B lista nera, K categoria), regolatore globale, tag personalizzati CLIP con prova sull'archivio, lista nera, categorie | App, Tagging |
| 3.9 | Misure e report: ricerca su 60.000 foto, tempi del tagging per foto (RAM++ su GPU e CPU) nel benchmark in-app, verifica XMP con exiftool | Tests |

## Decisioni tecniche prese in autonomia (da registrare in `docs/progress.md` e nella specifica)

| # | Decisione | Motivo |
|---|---|---|
| F3-1 | RAM++ **fp32** da `CannotFindObject/RAM_ONNX` (revisione `6c4ca81`, Apache-2.0, SHA-256 `4197a2cd…`), copia identica `imagent/ram-plus-onnx` come mirror; la variante int8 esce dal manifest | Laboratorio 13 ott 2026 (`verify_ramplus.py`, 52 foto reali): stesso insieme di tag dell'originale PyTorch su 52/52 foto, Δ probabilità < 0,001; la int8 dava 12/52 (§5) |
| F3-2 | Etichette italiane dei 4.585 tag RAM++ preparate durante lo sviluppo con un LLM e incluse nell'applicazione (`vocabulary/ramplus.tsv`), tutte "non riviste": finché l'utente non le approva si scrive solo l'inglese. La tavola 5c parla di "LLM locale": un modello linguistico locale richiederebbe gigabyte e GPU per 500 parole e darebbe risultati non riproducibili | Stesso risultato per l'utente (traduzione da rivedere), nessun download, funziona su sola CPU; la revisione resta all'utente (§8) |
| F3-3 | Categorie predefinite (Persone, Luoghi, Natura, Veicoli, Eventi, Animali) assegnate a ogni tag nello stesso file; modificabili nel Vocabolario (K) | §8 "mappatura sul nucleo": la mappa serve già prima che il nucleo si formi |
| F3-4 | Ricerca testuale con FTS5 di SQLite (tabella esterna sincronizzata dal catalogo), vettoriale con scansione in memoria degli embedding (60.000 × 512 float = 120 MB, prodotto scalare SIMD con `System.Numerics.Vector<T>` della libreria standard, nessuna dipendenza in più) dietro `ISearchIndex` | Scansione esatta in decine di millisecondi a 60.000 foto: nessun indice approssimato da mantenere; PostgreSQL/pgvector resta possibile dietro la stessa interfaccia |
| F3-6 | Copie esatte proposte solo dentro la stessa libreria; le copie dello stesso contenuto in librerie diverse sono backup | Proporre di spostare un backup sarebbe un errore; l'utente aggiunge spesso un disco di backup come libreria |
| F3-5 | Quasi-duplicati: candidati con coseno CLIP ≥ 0,95 confermati da hash percettivo (dHash 64 bit sulla miniatura, distanza ≤ 10); coppie RAW+JPEG escluse; proposta di quale tenere: RAW o file più grande, con tag manuali, più vecchio | CLIP da solo unisce scatti simili di una raffica; l'hash da solo perde ritagli e ridimensionamenti |

## Rischi

| Rischio | Mitigazione |
|---|---|
| RAM++ fp32 su sola CPU (~1 s/foto) | Livello B spento per default su sola CPU (§5), misura nel benchmark in-app |
| Traduzioni sbagliate | Mai scritte finché non riviste; revisione rapida a righe (5c) |
| DataGrid con Avalonia 12 | Verifica del pacchetto all'inizio della 3.8; alternativa: ListBox virtualizzato a colonne |
| Prestazioni FTS + filtri su 60.000 foto | Misura nella 3.5 prima dell'interfaccia; indici sulle colonne dei filtri |

## Dati

Riferimenti di parità RAM++ fp32 in `PHOTOFLOW_TESTDATA/lab/ramplus` (52 foto). Catalogo sintetico da 60.000 foto generato dai test. Etichette italiane nel repository (testo, non dati personali).
