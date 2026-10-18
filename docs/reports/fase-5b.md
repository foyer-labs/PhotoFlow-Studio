# Report di fine fase 5b — Didascalie in italiano (livello D, Qwen2.5-VL-7B)

Branch `phase/5b-vlm`. Piano: [docs/plans/fase-5b.md](../plans/fase-5b.md) (decisioni dell'utente U1-U5, tecniche D5b-1..D5b-8). Le misure locali usano le foto in `PHOTOFLOW_TESTDATA` (fuori dal repository); nel report ci sono solo numeri aggregati, nessuna didascalia di foto personali.

## Checklist dei criteri di accettazione (piano 5b)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Pesi rilevati, verificati e convertiti dall'app | ✅ Superato (test e conversione reale) · 🔍 prova dall'interfaccia a mano | **`VlmConversionTests`** (CI, Python finto): file mancante o diverso → stop alla verifica senza toccare Python; senza Python → messaggio con le istruzioni; poi ambiente, pacchetti, conversione, avanzamento, modello installato. I pacchetti non si reinstallano se i requisiti non cambiano. Una conversione fallita lascia il modello di prima. La cartella di lavoro non resta mai; i pesi originali non vengono toccati. **`VlmRealConversionTests`** (locale): conversione reale guidata dal codice dell'app, come la fa l'utente: Python 3.12 trovato con `py`, ambiente nuovo, pacchetti fissati, esportazione e modello installato in **9 min 44 s** (pacchetti dalla cache di pip: su un PC nuovo si aggiunge lo scaricamento di ~5 GB); cartella di lavoro rimossa |
| 2 | Didascalie italiane prodotte dal processo separato | ✅ Superato | **`VlmClientTests`** (CI): protocollo con un processo finto (risposte, righe estranee, id in ritardo, chiusura). **`VlmCaptionerTests`** (locale): il vero `PhotoFlow.Vlm` produce una didascalia italiana; la stessa foto dà la stessa didascalia; i JPEG temporanei vengono rimossi; un captioner nuovo avvia un processo nuovo. **Verifica approssimata (D5b-7)** su 10 foto locali contro PyTorch: vedi Misure |
| 3 | I5: didascalia dell'utente mai sostituita; C e D convivono | ✅ Superato | **`CaptionInvariantTests`** (CI): la didascalia italiana dell'utente resta, quella inglese segue ancora il modello; l'italiano viene per primo. **`CaptionXmpTests`** (CI, exiftool): `dc:description` x-default = italiano, `Description-en` = inglese. **`ReanalysisTests`** (CI): ogni didascalia si confronta col modello della sua lingua; quella dell'utente e una lingua senza modello attuale non si ricalcolano |
| 4 | Nessun impatto senza GPU adatta | ✅ Superato | **`VlmAvailabilityTests`** (CI): senza CUDA, con meno di 12 GB (anche 8 GB) o con memoria sconosciuta il livello D è disattivato. Nell'import la casella D resta spenta con il motivo; in Impostazioni la conversione si ferma con il requisito. **`GpuTurnsTests`** (CI): la passata D aspetta le analisi in corso e trattiene le nuove fino alla fine. **`VlmCaptionStageTests`** (CI): un modello che non parte ferma la passata dopo 3 tentativi senza far fallire il job |

## Cosa è stato fatto (tappe 5b.0-5b.5)

| Tappa | Contenuto |
|---|---|
| 5b.0 | Spec §5, §8, §17 e CLAUDE.md §3 con l'eccezione U4; test prima del codice (I5 per due lingue, XMP lang-alt) |
| 5b.1 | Laboratorio: conversione reale con la ricetta Olive (MIT) adattata al 7B; versioni fissate (`tools/vlm/requirements.txt`); verifica approssimata `tools/vlm/parity.py` |
| 5b.2 | Impostazioni › Modelli, riga "da convertire": elenco dei file originali, cartella apribile, **Verifica e converti** con avanzamento e annulla, istruzioni se manca Python, log in `logs/vlm-convert-*.log`, eliminazione dell'ambiente Python con conferma |
| 5b.3 | `PhotoFlow.Vlm` (.NET 10, ONNX Runtime GenAI 0.17.1 CUDA): JSON su stdin/stdout, un'immagine per richiesta, greedy, 128 token al massimo. Lato app `VlmCaptioner` (lingua `it`): avvio alla prima didascalia, riavvio dopo un errore, chiusura con l'app o al cambio dei modelli |
| 5b.4 | Livello D come **passata a fine job** (`VlmCaptionStage`, dopo i numeri di gara) e nella rigenerazione, con la GPU libera dagli altri modelli (`GpuTurns`); catalogo per lingua, XMP lang-alt, Consultazione (italiano sopra, inglese sotto), procedura guidata, benchmark ("Didascalie D", misurate nello stesso modo) |
| 5b.5 | Misure (sotto), report |

## Misure

| Misura | Valore |
|---|---|
| Conversione (RTX 3090, 128 GB di RAM) | ~10 min di esportazione; modello 6,5 GB (embedding 1,09 GB, testo int4 4,05 GB, visione fp16 1,35 GB); picco di RAM ~34 GB; file intermedi 27 GB, rimossi alla fine |
| Didascalia D, solo il processo | 1,4-2,2 s per foto (mediana 1,5 s), 33-56 token; avvio e caricamento ~5 s |
| VRAM del solo processo D | 8,5 GB dopo il caricamento, picco 10,1 GB (immagini a 1024 px; a 768 px 9,9 GB) |
| Verifica approssimata (D5b-7), 10 foto, stesso prompt e stessa immagine | Didascalie identiche **0/10**; token in comune **53%** in media (33-78%); prefisso comune 8,6 token in media; tempi: int4 1,5 s, PyTorch bf16 2,4 s. All'esame a occhio le didascalie dicono le stesse cose con parole diverse (stessi soggetti e oggetti, stessi testi letti nella scena) |
| Analisi completa, 24 foto, 4 lavori (D-FINE, buffalo\_l, CLIP, RAM++, Florence-2) | 35,0 s (1.460 ms per foto); picco di sistema 15,6 GB |
| Stessa analisi con il livello D per foto (prima versione, scartata) | +885 ms per foto; picco di sistema **22,3 GB** |
| Passata D a fine job, GPU libera (24 foto, avvio del processo compreso) | 47,5 s (**1.981 ms per foto**); picco **+10,6 GB** sopra il desktop (12,0 GB di sistema con 1,4 GB già usati da Windows) |

## Decisioni tecniche prese durante la fase

| # | Decisione | Motivo |
|---|---|---|
| D5b-4 rivista | **GPU NVIDIA ≥ 12 GB** (era ≥ 8 GB). P16 aggiornata | Misura: il 7B int4 da solo arriva a ~10 GB; su una scheda da 8 GB non si carica. L'utente ha indicato di non forzare e di cambiare la specifica. Provati senza risultato: arena CUDA "come richiesto", embedding su CPU (GenAI va in crash), immagini a 768 px (−240 MB) |
| D5b-9 | La ricetta legge i pesi del linguaggio col loro nome nel checkpoint (`model.*` → `language_model.*`), e `convert.py` controlla che l'embedding esportato dia i vettori del checkpoint | Con transformers 5 quei pesi restavano casuali, con un semplice avviso: il modello rispondeva subito con la fine del turno. Il controllo ferma una conversione sbagliata prima che arrivi all'app |
| D5b-10 | Modello di testo senza embedding (`exclude_embeds`) | ONNX Runtime GenAI 0.17 per Qwen2.5-VL vuole l'embedding come modello a parte |
| D5b-11 | Olive installato dall'archivio sorgente del commit fissato, non da git | Nessun git richiesto sul PC dell'utente; stesso codice (il commit è 116 commit dopo la 0.13.0 su PyPI) |
| D5b-12 | La cartella di lavoro (27 GB) si svuota prima e si elimina dopo ogni conversione, tramite `InternalFileCleaner.DeleteInternalDirectory` (nuovo, con lo stesso controllo dei collegamenti). Lo spazio richiesto somma le cartelle sullo stesso disco (~45 GB) | B1: solo il codice di pulizia dell'app elimina; lo script Python non cancella nulla |
| D5b-13 | KV cache del processo D limitata a 2.048 token | Un'immagine di 1024 px sono al massimo ~1.370 token; con 16.384 servivano 1,4 GB in più |
| D5b-14 | `OgaHandle` tenuto per tutta la vita del processo | Senza, il processo andava in crash alla chiusura |
| D5b-15 | **Il livello D non gira per foto ma in una passata a fine job** (e a fine rigenerazione). La passata aspetta le analisi in corso, trattiene le nuove, scarica dalla GPU i modelli dell'analisi, avvia il VLM e lo chiude alla fine. Dopo 3 errori di fila si ferma senza far fallire il job | Per foto, insieme agli altri modelli, il picco era di 22,3 GB; da solo il VLM sta in 12 GB. Rispetta l'indicazione di non forzare |

## Da fare per te

1. **Conversione dall'interfaccia**: Impostazioni › Modelli, riga "Descrizioni · Qwen2.5-VL-7B". Apri la cartella, copia i 14 file originali e premi **Verifica e converti**. Controlla: avanzamento, possibilità di annullare, messaggio finale e log.
2. **Import con il livello D**: nella procedura guidata spunta "D · Didascalie in italiano"; in Consultazione la didascalia italiana va sopra e quella inglese sotto.
3. **XMP**: con la preferenza "didascalie in `dc:description`" attiva, in Bridge o Lightroom la descrizione deve essere quella italiana.
4. (Facoltativo) **Elimina l'ambiente Python** dalla stessa riga (circa 5 GB). I pesi originali (16,6 GB) si possono cancellare a mano dalla cartella, quando non servono più.

## Limiti noti e rinvii (backlog §17)

- La verifica è approssimata (eccezione U4): l'int4 a pesi arrotondati (RTN) cambia le parole in quasi tutte le didascalie. Una quantizzazione calibrata (AWQ/GPTQ) o l'fp16 sulle schede da 24 GB andrebbero misurate.
- Il livello D richiede 12 GB di VRAM; le schede da 8-10 GB restano al livello C. Su una scheda da 12 GB il margine dipende da quanta memoria usa già Windows (qui 1,4 GB: restano ~0,3 GB). Se non basta, la passata si ferma e lo scrive in `logs/vlm-*.log`; le foto aspettano la passata successiva.
- L'analisi completa senza D ha un picco di 15,6 GB su questa scheda da 24 GB (gli arena di ONNX Runtime crescono con la memoria libera). Su schede più piccole va misurata: è una verifica per le fasi precedenti, non introdotta qui.
- I pesi originali non si eliminano dall'app (sono file messi dall'utente).
