# Piano della fase 5b — Didascalie VLM (livello D) con Qwen2.5-VL-7B

Stato: **approvato dall'utente** il 13 ott 2026, **completato** (report in `docs/reports/fase-5b.md`). Branch: `phase/5b-vlm`. Nasce dalla revisione della decisione F5-2 (livello D rinviato), che l'utente ha deciso di non confermare.

Riferimenti: spec §5 (livelli di tagging, modelli), §8 (didascalie), §17 P16 (VLM solo con GPU ≥ 12 GB, rivista: era ≥ 8 GB); CLAUDE.md §3 (inferenza tramite `IInferenceProvider`).

## Decisioni dell'utente (13 ott 2026)

| # | Decisione |
|---|---|
| U1 | Livello D con **Qwen2.5-VL-7B-Instruct** (Apache-2.0, scheda Hugging Face verificata il 13 ott 2026; il 3B è escluso: licenza "qwen-research", non commerciale). **Rivista il 15 ott 2026** su richiesta dell'utente: **Qwen3.5-9B** (Apache-2.0, multimodale nativo) per le nuove installazioni; Qwen2.5-VL resta dove è già convertito finché l'utente non sceglie il nuovo (spec §5) |
| U2 | L'utente scarica a mano i pesi originali in una cartella; **l'app li rileva e li converte** in ONNX |
| U3 | La conversione usa **Python già installato** sul PC; se manca, l'app spiega come installarlo |
| U4 | Il modello gira con **ONNX Runtime GenAI in un processo separato**, dietro l'interfaccia delle didascalie: **eccezione a CLAUDE.md §3**, con verifica approssimata invece della parità esatta |
| U5 | Il livello D scrive **solo in italiano**; il livello C (Florence-2, inglese) resta; l'italiano si mostra per primo |

## Decisioni tecniche proposte

| # | Decisione | Motivo |
|---|---|---|
| D5b-1 | **Pesi originali.** Vanno in `Modelli/qwen2.5-vl-7b-source` (cartella indicata e apribile da Impostazioni › Modelli), fissati a una revisione di `Qwen/Qwen2.5-VL-7B-Instruct`. Il rilevamento verifica `config.json` (`qwen2_5_vl`) e lo SHA-256 di ogni file contro l'elenco della revisione | Il modello giusto, completo e intatto prima di convertire (~16 GB) |
| D5b-2 | **Conversione.** L'app crea un ambiente Python privato in una sua cartella interna (`python -m venv`). Installa versioni fissate di `onnxruntime-genai-cuda`, `torch` e `transformers` e lancia il Model Builder di ONNX Runtime GenAI (int4, CUDA), con avanzamento e log. Alla fine si può eliminare l'ambiente (solo file interni, B1). Lo script è in `tools/vlm/` e viene incluso nell'app | Un ambiente controllato senza toccare il Python dell'utente; int4 per contenere la VRAM (misura in D5b-4) |
| D5b-3 | **Esecuzione.** Un piccolo eseguibile `PhotoFlow.Vlm` (.NET 10, `Microsoft.ML.OnnxRuntimeGenAI.Cuda` 0.17.x sull'ONNX Runtime 1.31 già in uso) riceve richieste JSON su stdin e risponde su stdout. Si avvia alla prima didascalia, resta vivo e si chiude con l'app. Lato app è un `IImageCaptioner` con lingua `it` | Le librerie native di GenAI non si mescolano con quelle dell'app; l'interfaccia resta quella del livello C |
| D5b-4 | Solo GPU NVIDIA con ≥ 12 GB di VRAM (P16); altrimenti il livello D resta disattivato con il motivo. **Rivista il 13 ott 2026** (era ≥ 8 GB): il 7B int4 da solo arriva a ~10 GB (caricamento 8,5 GB, +1,6 GB in generazione); l'utente ha indicato di non forzare e di cambiare la specifica. Insieme ai modelli dell'analisi si superano i 21 GB: per questo il livello D gira in una passata a fine job, con la GPU libera (+10,6 GB) | P16 |
| D5b-5 | **Generazione.** Prompt fisso in italiano ("Descrivi questa foto in italiano in una o due frasi, in modo oggettivo, senza inventare nomi di persone o luoghi."), decodifica greedy, al massimo 128 token, immagine al massimo di 1024 px di lato lungo | Didascalie brevi e riproducibili; tempo per foto limitato |
| D5b-6 | **Catalogo e XMP.** La didascalia italiana si salva in `Captions` (lingua `it`, versione `qwen2.5-vl-7b@<rev>/int4`); quella scritta dall'utente non viene mai sostituita (I5). In XMP `dc:description` è x-default = italiano, con l'inglese come lingua alternativa quando c'è. La sincronizzazione legge x-default | Una didascalia per lingua, l'italiano per primo (U5) |
| D5b-7 | **Verifica** (eccezione U4). Riferimenti dal modello PyTorch originale (greedy, stesso prompt e stessa immagine) su 10 foto locali. Si misurano didascalie identiche, token in comune e tempi; il risultato e l'esame a occhio vanno nel report. Niente soglia di parità esatta: l'int4 cambia i pesi | La parità esatta è impossibile con pesi int4; la misura rende visibile quanto ci si allontana |
| D5b-8 | Nell'import il livello D si sceglie come C; la rigenerazione lo tratta come parte "didascalie" (italiano) | Stesso flusso delle altre parti |

## Criteri di accettazione (proposti per la fase 5b)

| Criterio | Evidenza prevista |
|---|---|
| Pesi rilevati, verificati e convertiti dall'app | Test con una cartella di pesi simulata (rilevamento e verifica SHA) e con un Python finto (comandi, avanzamento, errori); conversione reale sul PC dell'utente (verifica manuale con istruzioni) |
| Didascalie italiane prodotte dal processo separato | Test d'integrazione del protocollo con un processo finto; prova reale (locale) su 10 foto con confronto con PyTorch |
| I5: didascalia dell'utente mai sostituita; C e D convivono | Test sul catalogo (due lingue, manuale protetta) e sull'XMP (x-default italiano, inglese alternativo) |
| Nessun impatto senza GPU adatta | Test: senza CUDA o con meno di 12 GB il livello D è disattivato con il motivo |

## Tappe

| Tappa | Contenuto |
|---|---|
| 5b.0 | spec §3, §5, §8, §17 e CLAUDE.md §3 con l'eccezione U4; test prima del codice (I5 per due lingue, XMP lang-alt) |
| 5b.1 | Laboratorio: conversione reale con il Model Builder, riferimenti PyTorch, tempi e VRAM; versioni fissate |
| 5b.2 | Rilevamento e verifica dei pesi, ambiente Python privato, conversione con avanzamento (Impostazioni › Modelli) |
| 5b.3 | `PhotoFlow.Vlm` e captioner lato app; ciclo di vita del processo |
| 5b.4 | Catalogo, XMP, Consultazione, import, rigenerazione, benchmark |
| 5b.5 | Misure, report, PR |

## Rischi

| Rischio | Mitigazione |
|---|---|
| Spazio su disco: ~16 GB di pesi + ~5 GB di ambiente + ~27 GB di file intermedi durante l'esportazione + ~6,5 GB di modello | Spazio controllato prima di iniziare; ambiente e pesi originali eliminabili dopo la conversione (con conferma) |
| Il Model Builder cambia tra le versioni | Versioni fissate e provate nel laboratorio; errore chiaro se la conversione fallisce |
| VRAM insieme agli altri modelli (CLIP, RAM++, Florence-2) | Il livello D gira con un solo lavoro alla volta; si può scaricare il resto durante una passata solo didascalie |
| Tempo: alcuni secondi per foto | Facoltativo, scelto per job; stima mostrata prima di avviare |
