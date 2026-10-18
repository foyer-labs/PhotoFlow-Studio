# Report di fine fase 3 — Tag, vocabolario, Consultazione, Duplicati

Data: 2026-10-13 · Branch: `phase/3-tags-search` · Piano: [`docs/plans/fase-3.md`](../plans/fase-3.md)

Fase svolta in autonomia su indicazione dell'utente (2026-10-12): le decisioni prese durante la fase sono in `docs/progress.md` e nella specifica (§4, §5, §8, §10, §17) e sono riassunte sotto.

## Checklist dei criteri di accettazione (§13)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Ricerca < 1 s su 60.000 foto | ✅ Superato (misura locale) | `SearchTests.Searches_on_sixty_thousand_photos_take_less_than_a_second` (`Performance`, Release, catalogo sintetico di 60.000 foto con 8 tag, 0-3 volti, date, fotocamere, rating ed embedding 512-d ciascuna): tutto 193 ms, persona 4 ms, persona + tag 10 ms, parola italiana 161 ms, cartella/evento 171 ms, significato 191 ms, persona + tag + significato 15 ms, date + fotocamera + rating 70 ms, simili 222 ms, **dopo una persona rinominata e 400 tag cambiati 263 ms**. La prima ricerca in assoluto costruisce l'indice testuale e carica gli embedding: 1,45 s, una volta sola. Correttezza: `SearchTests.Filters_words_meaning_and_similar_photos` (CI) |
| 2 | Keyword IT+EN e gerarchiche lette da Bridge | ✅ Superato (exiftool) · 🔍 verifica in Bridge a mano | `TagXmpTests` (CI): XMP scritto e riletto con exiftool — `dc:subject` con chiave inglese e etichetta italiana rivista, tag non rivisto solo in inglese, lista nera mai scritta, `lr:hierarchicalSubject` `Natura\|Tramonto` (italiano una volta rivisto, inglese prima), evento `Eventi\|Gita al lago`; un tag tolto esce dal file, le keyword di altri programmi restano. La lettura in Bridge è il punto 2 qui sotto |
| 3 | Rianalisi: tag manuali e soppressi preservati | ✅ Superato | `TagInvariantTests` (scritto prima del codice): nuova analisi con altro modello e altre confidenze → il tag manuale resta, il soppresso non torna (né da RAM++ né da CLIP), i tag di un'altra fonte restano; solo l'utente riattiva un soppresso; anche un tag manuale tolto dall'utente resta soppresso. Anche `TaggingStageTests` (tag personalizzato tolto non riassegnato) e `BrowseKeyboardTests` (✕ in Consultazione → soppresso) |

Test: 391, di cui 36 locali (parità, qualità, prestazioni) saltati senza `PHOTOFLOW_TESTDATA`; gli altri girano in CI su Windows e Linux. In locale (Release, CUDA): `RamPlusParityTests` superato su CPU (ingresso e probabilità identici al riferimento) e GPU (Δ probabilità massimo 1,7·10⁻⁶).

## Cosa è stato fatto (tappe 3.0-3.9)

| Tappa | Contenuto |
|---|---|
| 3.0 | Test prima del codice: I5 per i tag (`TagInvariantTests`) |
| 3.1 | RAM++ fp32 (F3-1): parità con l'originale PyTorch nel laboratorio (52/52 foto con lo stesso insieme di tag) e dell'adattatore C# con i riferimenti (`tools/lab/ramplus_refs.py`); manifest aggiornato, la int8 esce |
| 3.2 | Catalogo dei tag (migrazione 0008), `ITagStore` con le regole I5 nell'SQL; vocabolario RAM++ con etichette italiane e categorie incluso e caricato all'avvio (F3-2, F3-3), lista nera predefinita minima |
| 3.3 | Pipeline: RAM++ per foto sull'anteprima già decodificata (livello B); fase di lotto con evento come tag e tag personalizzati CLIP dagli embedding salvati (livello A); livelli scelti nell'import (B per default solo con GPU) |
| 3.4 | XMP: chiave inglese sempre, italiano se rivisto, gerarchia `Categoria\|Etichetta`; `XmpSync` ricorda le keyword scritte da PhotoFlow (migrazione 0009) e toglie solo quelle |
| 3.5 | Ricerca (F3-4): filtri esatti, parole FTS5 (nomi file e cartelle, etichette nelle due lingue, persone), significato e simili con scansione esatta degli embedding; indice aggiornato foto per foto da trigger (migrazioni 0010, 0012) |
| 3.6 | **Consultazione** (4d, 4e, 4e2): gettoni + testo libero, filtri, griglia virtualizzata, dettaglio con volti evidenziati, tag modificabili (T, ✕), stelle 1-5, Ctrl E Photoshop (editor scelto nelle Impostazioni), Ctrl ↵ app predefinita, Ctrl Maiusc E Esplora risorse, S simili, Spazio anteprima grande; nessun risultato con le vie d'uscita; ricerca dalla barra del titolo |
| 3.7 | **Duplicati** (4f, 4g; F3-5, F3-6): copie esatte nella stessa libreria, quasi-duplicati CLIP + hash percettivo, proposta di quale tenere, confronto affiancato, spostamento in `_duplicati` (mai cancellazioni), "non duplicati" ricordati (migrazione 0011) |
| 3.8 | **Vocabolario** (5c, 5d, 5d2): nucleo dei 500 tag più frequenti dopo 1.000 foto con tag, revisione a righe (Invio, F2, B, K, Ctrl Z), regolatore globale, esempi con confidenza più bassa; tag personalizzati CLIP con prova sull'archivio e "salva e rianalizza"; lista nera; categorie |
| 3.9 | Misure (sotto); tag RAM++ nel benchmark in-app |

## Misure

**Set locale** `PHOTOFLOW_TESTDATA/photos` (230 foto), Release, RTX 3090, CUDA, 4 analisi in parallelo (background), `Phase3PerformanceTests`:

| Misura | Valore |
|---|---|
| Analisi con D-FINE M, buffalo\_l, CLIP, impronte ArcFace e tag RAM++ | 55,4 s → **4,15 foto/s** (fase 2 senza RAM++: 6,9 foto/s) |
| Stima per 57.130 foto (archivio della tavola 5f) | 3,8 h in background |
| Tag per foto | media 11,2, massimo 21; 420 tag distinti |
| Ricerche sul catalogo reale ("moto", "persona", "montagna", "folla") | 1-17 ms |

I tag più frequenti del set (person, building, car, man, motorcycle, race track, …) sono nel report locale `PhotoFlowTestData\lab\performance\phase3-*.md` per il controllo a mano. Ricerca su 60.000 foto: tabella del criterio 1.

## Decisioni tecniche prese durante la fase

Registrate in `docs/progress.md`, nel piano e nella specifica:

- **F3-1** RAM++ fp32 (`CannotFindObject/RAM_ONNX`, mirror `imagent/ram-plus-onnx`) al posto della int8.
- **F3-2** Etichette italiane dei 4.585 tag preparate durante lo sviluppo con un LLM e incluse, tutte "non riviste" (la tavola 5c dice "LLM locale": un modello locale richiederebbe gigabyte e GPU per poche centinaia di parole). Finché non sono riviste si scrive solo l'inglese.
- **F3-3** Categoria predefinita per ogni tag nello stesso file; lista nera predefinita minima (image, photo, picture, snapshot, file photo, screenshot, stock), applicata solo al primo inserimento.
- **F3-4** Ricerca su SQLite: FTS5 + scansione esatta degli embedding in memoria con `Vector<T>` della libreria standard; indice testuale aggiornato da trigger.
- **F3-5** Quasi-duplicati: CLIP ≥ 0,95 confermato dall'hash percettivo (≤ 10 bit).
- **F3-6** Copie esatte proposte solo dentro la stessa libreria: tra librerie diverse sono backup.
- **Keyword scritte da PhotoFlow** ricordate per toglierle quando il tag va via, senza mai toccare quelle di altri programmi.
- **Gerarchia** in italiano una volta rivisto, in inglese prima (spec §4).
- **Trigger senza conflitti**: in SQLite la politica di conflitto dell'istruzione esterna (upsert) prevale su `INSERT OR IGNORE` dentro un trigger; trovato dai test e corretto con `NOT EXISTS`.

**Scostamenti dalle tavole** (nel backlog §17): Vocabolario con elenco a colonne e modifica nel pannello invece di DataGrid; in Duplicati manca lo zoom sincronizzato e "Mostra esclusi"; in Consultazione l'evento è un gettone del campo invece di un menu "Evento ▾" e la griglia non ha selezione multipla.

## Da verificare a mano (per te)

1. **Modelli**: Impostazioni › Modelli → RAM++ fp32 (1,86 GB, Apache-2.0): scaricalo, poi un import con "B · Tag generici" spuntato.
2. **Bridge / Lightroom**: dopo la conferma di una revisione, apri una foto in Bridge: parole chiave in inglese e, per i tag rivisti nel Vocabolario, in italiano; gerarchia `Natura|Tramonto`; l'evento come parola chiave. Togli un tag in Consultazione (✕) e verifica che sparisca da Bridge, mentre le parole chiave che avevi messo tu restano.
3. **Consultazione**: Ctrl K, scrivi una persona o un tag e scegli il suggerimento; poi una frase ("bambini al mare"): ordinamento per significato. Ctrl E dopo aver scelto Photoshop in Impostazioni › Generali.
4. **Vocabolario**: dopo 1.000 foto con tag compare il nucleo; prova Invio, F2, B, K e Ctrl Z su alcune righe. Le traduzioni sono da rivedere: segnala quelle sbagliate più frequenti.
5. **Tag personalizzati**: crea "Lago di Como" con una descrizione inglese, Ctrl T per la prova, Ctrl S per assegnarlo.
6. **Duplicati**: controlla un gruppo con una copia in un'altra cartella; Invio sposta in `_duplicati` (verifica in Esplora risorse che il file sia stato spostato, non cancellato).
7. Restano validi i punti manuali delle fasi precedenti.

## Limiti noti e rinvii

Tag personalizzati e regolatore globale richiedono un riavvio per il regolatore (letto all'avvio). Le etichette italiane sono da rivedere. La prima ricerca dopo l'avvio carica gli embedding in memoria (~1,5 s a 60.000 foto).
