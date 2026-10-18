# Piano della fase 4 — OCR motorsport, elenco iscritti, vista Numeri

Stato: **completata** (13 ott 2026; report in `docs/reports/fase-4.md`). Eseguita in autonomia su indicazione dell'utente del 2026-10-12. Branch: `phase/4-motorsport`.

Riferimenti: spec §3 (interfacce, `IInferenceProvider`), §5 (OCR PP-OCR), §6 (profilo Motorsport), §8 (OCR motorsport, tag `Motorsport|…`), §9 (`OcrResults`, `EntryLists`), §12bis (Revisione 2c, scorciatoie della vista Numeri), §13 (fase 4); D18.

## Contenuto (spec §13)

OCR dei numeri di gara sul ritaglio di ogni vettura rilevata da D-FINE; elenco iscritti (import CSV/Excel, inserimento manuale, riutilizzabile su più eventi); profilo Motorsport con OCR attivo; vista "Numeri" della revisione (finora visibile e disattivata) con correzione rapida da tastiera; numeri, piloti e team negli XMP.

**Fuori dalla fase 4**: didascalie, rigenerazione completa, esportazione, sincronizzazione XMP esterna (fase 5).

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13) | Evidenza prevista |
|---|---|
| Numeri riconosciuti solo se presenti in elenco; casi ambigui in "da rivedere" | Test `Unit` sulla scelta del numero (letture OCR con confidenze → numero in elenco, candidati multipli 6/9 e 1/7, parziali, numeri estranei di sponsor e targhe ignorati, senza elenco solo il numero) e `Integration` sulla fase completa con immagini sintetiche di vetture numerate (D-FINE simulato, OCR reale se il modello c'è); propagazione a maggioranza nella raffica; vista Numeri da tastiera |
| Equipaggi: solo numero nell'XMP | Test scritto prima del codice: con equipaggio (2+ piloti) l'XMP contiene `Motorsport\|Numero N` e il team, mai i nomi; con pilota singolo anche `Motorsport\|Pilota\|Nome`; letto con exiftool |

## Tappe

| Tappa | Contenuto | Progetti |
|---|---|---|
| 4.0 | Test prima del codice: D18 (equipaggio → solo numero e team nell'XMP), numeri fuori elenco mai scritti, I5 (numero confermato dall'utente mai sostituito da una nuova lettura) | Tests |
| 4.1 | OCR (F4-1): rilevamento testo DB (PP-OCRv4 det) e riconoscimento CTC (en PP-OCRv3 rec, dizionario letto dai metadati del modello) come adattatori su `IInferenceProvider` (non RapidOcrNet, che userebbe sessioni proprie: CLAUDE.md §3); parità con RapidOCR Python su ritagli reali (`tools/lab/ocr_refs.py`) | Tagging, Inference, Core |
| 4.2 | Elenco iscritti: tabelle `EntryLists` ed `Entries` (numero, piloti, team, categoria, vettura), import CSV (separatore e intestazioni riconosciuti) ed Excel `.xlsx` (F4-2), inserimento manuale, collegamento a un job | Data, Core, Tagging |
| 4.3 | Fase OCR: solo per le foto col profilo che chiede l'OCR; ritaglio di ogni vettura (auto, moto, camion, bus) dai soggetti D-FINE, letture numeriche, scelta del numero (F4-3), risultati in `OcrResults` e numeri per vettura; propagazione a maggioranza nelle raffiche (barriera di lotto) | Analysis, Tagging, Data |
| 4.4 | Tag e XMP: `Motorsport\|Numero N`, `Motorsport\|Pilota\|Nome`, `Motorsport\|Team\|Nome` dall'elenco; equipaggio solo numero + team (D18), nomi nel catalogo e ricercabili | App, Tagging |
| 4.5 | Vista Numeri (2c): da risolvere (ambigui, parziali), in elenco per numero, altro (nessun numero, fuori elenco); ritagli ingranditi con le letture; 0-9 digita, Invio conferma e avanti, Canc nessun numero, ↑ ↓ numero in elenco, Ctrl Z, propagazione alla raffica; pannello elenco iscritti (import, modifica) | App |
| 4.6 | Misure e report | Tests |

## Decisioni tecniche prese in autonomia (da registrare in `docs/progress.md` e nella specifica)

| # | Decisione | Motivo |
|---|---|---|
| F4-1 | Modelli RapidOCR fissati su `SWHL/RapidOCR@1cfba2e` (Apache-2.0): `ch_PP-OCRv4_det_infer.onnx` (4,7 MB) e `en_PP-OCRv3_rec_infer.onnx` (9 MB, cifre e lettere latine, dizionario nei metadati); adattatori nostri su `IInferenceProvider` | Stessa pipeline di inferenza (CUDA/CPU, parità) degli altri modelli; totale 14 MB; la v5 latina disponibile (`monkt/paddleocr-onnx`) ha un rilevatore da 88 MB |
| F4-2 | Excel `.xlsx` letto con la libreria standard (zip + XML: prima scheda, stringhe condivise e in linea, numeri) — **cambiata in esecuzione**: prima era `DocumentFormat.OpenXml` | Per leggere una tabella basta la libreria standard: nessuna dipendenza (~5 MB) in più; coperta da test con un file come lo scrive Excel |
| F4-3 | Scelta del numero: letture solo cifre (1-3) del ritaglio; accettato il numero in elenco con la confidenza più alta se supera la soglia (0,7) e distacca il secondo candidato in elenco (0,1); altrimenti candidati → "da rivedere"; numeri non in elenco ignorati (senza elenco: il numero letto con confidenza ≥ 0,85) | §8: nessun numero scritto se non certo |
| F4-4 | Riconoscitore OCR sempre su CPU, rilevatore sul backend migliore (aggiunta in esecuzione) | Misurato: 27 ms per testo su GPU contro 3 ms su CPU (ingressi piccoli di larghezza sempre diversa); rilevatore 68 ms GPU contro 205 ms CPU |
| F4-5 | Rifiniture della scelta (aggiunte in esecuzione): penalità della cifra confusa 0,5 quando anche il numero letto è in elenco; senza elenco vale solo un numero letto da solo | Un 9 nitido diventava "da rivedere" se il 6 era iscritto (test d'integrazione); al salone moto "959 Panigale" e "G 310" diventavano numeri certi |

## Rischi

| Rischio | Mitigazione |
|---|---|
| Numeri piccoli o inclinati sulle vetture | OCR sul ritaglio ingrandito della vettura; vista Numeri per la correzione rapida; propagazione nella raffica |
| D-FINE (COCO) non riconosce monoposto e prototipi | Verifica sulle foto in pista (§17); senza vettura rilevata l'OCR gira sull'intero fotogramma |
| Nessun set etichettato di numeri | Misura qualitativa sul set locale (motorsport del 2015) e verifica manuale nella vista Numeri |
