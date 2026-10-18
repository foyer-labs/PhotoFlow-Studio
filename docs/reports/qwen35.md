# Report — passaggio del livello D a Qwen3.5-9B

Data: 15 ottobre 2026 · Richiesta dell'utente · Decisione U1 rivista (spec §5, `docs/plans/fase-5b.md`)

## Perché

Qwen2.5-VL-7B (gennaio 2025) non è più il modello migliore della sua taglia. Ricerca sulle fonti (schede Hugging Face, rilasci di ONNX Runtime GenAI, `microsoft/olive-recipes`): **Qwen3.5-9B** (marzo 2026, Apache-2.0, multimodale nativo) è supportato da ONNX Runtime GenAI 0.17.1, già fissato, e ha una ricetta Olive ufficiale per CUDA. Scartati Qwen3-VL-8B (il runtime ne perde le feature DeepStack), Gemma 4 (piccoli più deboli, 12B non supportato) e altri modelli non supportati o con licenze meno libere.

## Cosa è cambiato

| Area | Modifica |
|---|---|
| Manifest | `qwen3.5-9b` (pesi a revisione fissa `c202236`, 14 file, SHA-256 verificati contro Hugging Face); `qwen2.5-vl-7b` resta |
| Scelta del modello | Il modello attivo è quello scelto in Impostazioni › Modelli; altrimenti Qwen2.5-VL se già convertito (nessun cambio silenzioso delle didascalie), altrimenti Qwen3.5. «Usa questo modello» propone la rigenerazione delle didascalie |
| Conversione | `tools/vlm/recipes/qwen3_5` (ricetta Olive `38e17a606cda`) accanto a `qwen2_5_vl`; `convert.py` sceglie dal `model_type` |
| Runner | `PhotoFlow.Vlm` riconosce il modello da `genai_config.json`; per Qwen3.5 la chat senza ragionamento (blocco think vuoto, come il template con `enable_thinking=false`) |
| Testi | Nota delle Impostazioni per modello (repository e dimensione dal manifest); «Qwen» al posto di «Qwen2.5-VL» dove valgono entrambi |

Correzioni alla ricetta ufficiale, trovate provando la conversione:

1. Il Model Builder 0.17.1 esporta anche la testa MTP del checkpoint, incompatibile con `prune_lm_head` → `exclude_mtp`.
2. Senza `exclude_embeds` costruisce la variante solo testo (`qwen3_5_text`), che il runtime non abbina agli altri modelli.
3. Fine della risposta solo su `<|endoftext|>`: una risposta di chat finisce con `<|im_end|>` → entrambi.
4. Immagine fino a 16 megapixel → limite a ~1 megapixel (l'app passa al massimo 1024 px).
5. Un peso del checkpoint non caricato era solo un avviso (restava casuale) → errore.

## Misure (RTX 3090, 12 foto dei dati di test a 1024 px, stesso prompt)

| | Qwen2.5-VL-7B | Qwen3.5-9B, embedding fp16 (ricetta) | **Qwen3.5-9B adottato** |
|---|---|---|---|
| Picco memoria GPU del modello | 10,2 GB | 11,7 GB | **9,8 GB** (tabella di embedding int8) |
| Tempo per foto (mediana) | 1,16 s | 0,88 s | **0,88 s** |
| Didascalie con frasi ripetute | — | 4/12 (greedy) | **0-2/12** (penalità 1,05) |
| Modello convertito | 6,05 GB | — | **6,05 GB** |
| Conversione su questo PC | ~15 min | — | **6,3 min** |

- **Memoria.** Con la tabella fp16 della ricetta il picco (11,7 GB) è troppo vicino ai 12 GB di P16. Le opzioni del runtime (arena, CUDA graph) non lo riducono; l'embedding su CPU manda in crash il runtime. La tabella quantizzata int8 a blocchi da 128 (1,9 → 1,0 GB) porta il picco a 9,8 GB: P16 resta valida. Con la tabella int8 10 didascalie su 12 sono identiche a quelle con la tabella fp16, le altre equivalenti.
- **Ripetizioni.** In greedy Qwen3.5 ripete parole o frasi; la penalità 1,05 in `genai_config.json` le elimina.
- **Qualità rispetto a Qwen2.5-VL (a occhio, 12 foto).** Qwen3.5 legge meglio le scritte (marchi sul pavimento, nomi su un cartello), coglie più dettagli e inventa meno marchi non leggibili; le didascalie sono un po' più lunghe (56-61 token contro 46).
- **Verifica approssimata contro PyTorch bf16 (D5b-7, `tools/vlm/parity.py`, 10 foto, stessa penalità).** Didascalie identiche 0/10; token in comune 43% in media; prefisso comune 2,5 token (Qwen2.5-VL in fase 5b: 0/10, 53%, 8,6). A occhio dicono le stesse cose, spesso con un attacco diverso («La foto mostra» / «Questa foto mostra»); il modello int4 coglie qualche dettaglio in meno dell'originale (per esempio una scritta su una parete). Tempi: int4 0,85 s, PyTorch 4,6 s.

## Test

- `VlmCaptionerTests` (Parity, locale): entrambi i modelli con il vero processo `PhotoFlow.Vlm`: didascalia italiana, stabile, senza blocco think — passati.
- `VlmModelChoiceTests` (Integration): il modello cambia solo quando l'utente lo sceglie.
- `ModelRegistryTests`: i due modelli del livello D nel manifest.
- Unit, Integration, Scenario: 416 passati, 3 saltati, 0 falliti.

## Stato sul PC dell'utente

Pesi scaricati (18 GB, verificati) e convertiti con il codice dell'app nel profilo dell'utente: Qwen3.5-9B risulta installato. Il modello attivo resta Qwen2.5-VL finché l'utente non preme «Usa questo modello» su Qwen3.5-9B.

## Verifiche manuali (per l'utente)

1. Impostazioni › Modelli: Qwen3.5-9B «Convertito ✓», Qwen2.5-VL «In uso».
2. «Usa questo modello» su Qwen3.5-9B, poi la rigenerazione proposta su un campione: confrontare le didascalie nuove con le vecchie.
3. Un import con il livello D: tempi e didascalie.
