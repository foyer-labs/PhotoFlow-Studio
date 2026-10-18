# Report di fine fase 4 — OCR motorsport, elenco iscritti, vista Numeri

Data: 2026-10-13 · Branch: `phase/4-motorsport` · Piano: [`docs/plans/fase-4.md`](../plans/fase-4.md)

Fase svolta in autonomia su indicazione dell'utente (2026-10-12). Le decisioni prese durante la fase sono in `docs/progress.md` e nella specifica (§5, §8, §9, §15, §17) e sono riassunte sotto.

## Checklist dei criteri di accettazione (§13)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Numeri riconosciuti solo se presenti in elenco; casi ambigui in "da rivedere" | ✅ Superato (test) · 🔍 qualità su foto in pista da verificare | **`NumberMatcherTests`** (Unit, CI): numero in elenco nitido → certo; sponsor (2024) e targhe ignorati; 6/9 e 1/7 → candidati "da rivedere"; letture parziali → numeri possibili; senza elenco solo un numero letto da solo e nitido ("959 Panigale" no); maggioranza della raffica. **`NumbersStageTests`** (Integration, CI): letture → elenco del job. Un 9 nitido è certo anche con il 6 iscritto. "6" incerto e foto illeggibile prendono il 9 dalla raffica. Lo sponsor "348" è fuori elenco e senza tag. Il numero dato dall'utente resta dopo una nuova esecuzione (I5). Ogni nome dell'elenco è cercabile. **`NumbersKeyboardTests`** (Integration, CI): N dalla Revisione apre la vista sul dubbio "6 o 9"; 9 + Invio lo risolve, Ctrl Z lo riporta, ↓ + Invio prende il primo candidato; 4 4 + Invio su una raffica illeggibile vale anche per l'altra foto; Canc = nessun numero; dopo la modifica dell'elenco i numeri si riabbinano e quelli dell'utente restano; Esc torna alla Revisione. **`OcrParityTests`** (Parity, locale): pipeline OCR identica a RapidOCR Python su 25 foto e 85 testi |
| 2 | Equipaggi: solo numero nell'XMP | ✅ Superato | **`MotorsportXmpTests`** (scritto prima del codice, Integration, CI, letto con exiftool): pilota singolo → `Motorsport\|Numero 27`, `Motorsport\|Pilota\|Marco Ferri`, `Motorsport\|Team\|Lario Corse`; equipaggio → solo numero e team, nessun nome nel file; numeri da rivedere o fuori elenco mai scritti; numero dell'utente mai sostituito. I nomi dell'equipaggio restano nel catalogo e si trovano nella Consultazione (`NumbersStageTests`: "Bianchi", secondo pilota di un equipaggio) |

Test: 406, di cui 38 locali (parità, qualità, prestazioni) saltati senza `PHOTOFLOW_TESTDATA`; gli altri girano in CI su Windows e Linux. In locale (CUDA): `OcrParityTests` superato; `Phase4PerformanceTests` (misure sotto).

## Cosa è stato fatto (tappe 4.0-4.6)

| Tappa | Contenuto |
|---|---|
| 4.0 | Test prima del codice: D18 (equipaggio → solo numero e team), numeri incerti mai scritti, I5 per i numeri |
| 4.1 | OCR (F4-1): rilevatore PP-OCRv4 e riconoscitore PP-OCRv3 inglese come adattatori su `IInferenceProvider`, dizionario dai metadati del modello; riferimenti con `tools/lab/ocr_refs.py` (RapidOCR 1.4.4) e parità esatta; manifest con SHA-256 |
| 4.2 | Elenco iscritti (migrazione 0013): import CSV (separatore riconosciuto, virgolette, UTF-8 o Latin-1) ed Excel `.xlsx` (F4-2), intestazioni italiane o inglesi, più piloti in una cella o in più colonne, equipaggi; un elenco serve più eventi ed è collegato al job |
| 4.3 | Fase OCR per le foto del profilo Motorsport: ritaglio delle vetture (le 6 più grandi), letture salvate per vettura; fase di lotto dei numeri (F4-3, F4-5) con propagazione nella raffica |
| 4.4 | Tag e XMP `Motorsport\|…` (D18); nomi, team e vettura dell'elenco cercabili (migrazione 0014, aggiornamento foto per foto) |
| 4.5 | **Vista Numeri** (2c) dalla Revisione (N), descritta in spec §8; elenco iscritti scelto anche nel passo 2 dell'import |
| 4.6 | Misure (sotto); riconoscitore spostato su CPU (F4-4) e un ridimensionamento doppio tolto |

## Misure

**Set locale** (35 foto di un salone moto del 2015 in `PHOTOFLOW_TESTDATA/photos`, JPEG 4128×2322 da telefono), Release, RTX 3090, CUDA, 4 analisi in parallelo dopo un riscaldamento, profilo Motorsport, `Phase4PerformanceTests`:

| Misura | Valore |
|---|---|
| Analisi senza OCR | 22,6 s → 1,55 foto/s (JPEG grandi senza anteprima incorporata) |
| Analisi con OCR | 26,0 s → 1,34 foto/s: **l'OCR costa 97 ms per foto** |
| Riconoscitore, per testo | 27 ms su GPU → **3 ms su CPU** (F4-4); rilevatore 68 ms su GPU, 205 ms su CPU |
| Vetture lette | 34 (1 per foto), 81 testi |
| Fase dei numeri (lotto) | 156 ms |
| Numeri senza elenco | prima di F4-5: "310" (BMW G 310 R) e "95" certi; con F4-5 le scritte di modelli dentro altro testo non sono più numeri certi |

Il set non contiene numeri di gara: la qualità sui numeri veri resta da verificare su foto in pista (punto 2 qui sotto).

## Decisioni tecniche prese durante la fase

Registrate in `docs/progress.md`, nel piano e nella specifica:

- **F4-1** Modelli RapidOCR fissati per commit (`SWHL/RapidOCR@1cfba2e`, Apache-2.0), 14 MB, con adattatori nostri invece di RapidOcrNet. Così l'OCR passa da `IInferenceProvider` (CUDA/CPU, parità) come gli altri modelli.
- **F4-2 (cambiata in esecuzione)** `.xlsx` letto con la libreria standard (zip + XML) invece di `DocumentFormat.OpenXml`: per una tabella basta, ed è una dipendenza in meno.
- **F4-3** Soglie della scelta del numero: confidenza 0,7, distacco 0,1, senza elenco 0,85.
- **F4-4** Riconoscitore su CPU: 9 volte più veloce che su GPU per i suoi ingressi piccoli di larghezza sempre diversa.
- **F4-5** Due rifiniture trovate dai test e dalle misure:
  - la cifra confusa pesa 0,5 quando anche il numero letto è in elenco (un 9 nitido diventava "da rivedere" col 6 iscritto);
  - senza elenco vale solo un numero letto da solo ("959 Panigale" non è un numero di gara).
- **Elenco del job**: si sceglie nell'import (solo con un profilo che legge i numeri) o nella vista Numeri; cambiarlo ripete la fase dei numeri, senza toccare quelli dell'utente.
- **Corretti strada facendo**:
  - le azioni rapide Cerca, Persone e Duplicati della Home erano ancora disattivate con "arriva con la fase 2/3";
  - la nota dei volti nell'import e l'introduzione dei modelli citavano le fasi.

**Scostamenti dalla tavola 2c** (nel backlog §17):

- nella vista Numeri ci sono i ritagli con le letture, non la foto intera con i riquadri ambra/grigi;
- la conferma con scrittura XMP si fa dalla Revisione;
- l'elenco si modifica come testo (una vettura per riga), non in tabella;
- non si mostra in quanti eventi è stato usato un elenco.

## Da verificare a mano (per te)

1. **Modelli**: Impostazioni › Modelli → riga "Numeri (OCR)", PaddleOCR (14 MB, Apache-2.0): scaricalo.
2. **Foto in pista**: importa una gara col profilo Motorsport e, nel passo 2, un elenco iscritti (CSV o Excel con numero, pilota, team). Poi, in Revisione, premi N:
   - i dubbi sono raggruppati ("6 o 9") e i numeri sono in elenco;
   - prova 0-9 + Invio, Canc, ↑ ↓, Ctrl Z e la propagazione alla raffica (P).
   - Verifica anche se D-FINE riconosce monoposto e prototipi (§17): senza vettura rilevata l'OCR legge l'intero fotogramma.
3. **Bridge**: dopo la conferma, una foto con pilota singolo ha `Motorsport|Numero N`, `Motorsport|Pilota|Nome` e `Motorsport|Team|Nome`. Una foto con equipaggio ha solo numero e team.
4. **Consultazione**: cerca il cognome del secondo pilota di un equipaggio. Deve trovare le sue foto anche se il nome non è nell'XMP.
5. Restano validi i punti manuali delle fasi precedenti.

## Limiti noti e rinvii

- Il ridimensionamento antialias del fotogramma per l'OCR costa ~65 ms a 1600 px (backlog).
- Il profilo automatico sceglie Motorsport per gruppo: con il profilo automatico l'elenco dell'import vale per i soli gruppi Motorsport.
