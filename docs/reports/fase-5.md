# Report di fine fase 5 — Rigenerazione, didascalie, esportazione, sincronizzazione XMP

Data: 2026-10-13 · Branch: `phase/5-regeneration` · Piano: [`docs/plans/fase-5.md`](../plans/fase-5.md)

Fase svolta in autonomia su indicazione dell'utente (2026-10-12). Le decisioni prese durante la fase sono in `docs/progress.md` e nella specifica (§4, §5, §8, §9, §15, §17) e sono riassunte sotto. Con questa fase si chiudono le fasi di §13.

## Checklist dei criteri di accettazione (§13)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Cambio modello volti: assegnazioni trasferite, nessuna persona persa | ✅ Superato (test e misura locale) | **`FaceModelChangeTests`** (scritto prima del codice, CI). I volti nuovi prendono la persona del volto vecchio che sovrappongono; le assegnazioni dell'utente restano dell'utente; un volto dell'utente non ritrovato resta; nessuna persona cancellata; copertina e prototipi passano al nuovo modello. **`ReanalysisTests`** (CI): rigenerazione completa con anteprima e conferma. **`Phase5PerformanceTests`** (locale, modelli reali): da buffalo\_l a YuNet + SFace su 230 foto, 38 persone, **nessuna persa**, le 38 assegnazioni dell'utente conservate, 151 assegnazioni automatiche trasferite |
| 2 | Esportazione e importazione su altro percorso con rimappatura riuscita | ✅ Superato | **`CatalogPackageTests`** (scritto prima del codice, CI): libreria, tag, persone con impronte, decisioni, didascalie, profilo e miniature in un `.pfcatalog`. Importato in un altro profilo con la libreria spostata: contenuti uguali, radice rimappata, XMP scritto nella nuova posizione e non in quella vecchia. Impronte ricifrate con la chiave locale (password giusta) o assenti (senza password); password sbagliata rifiutata; originale intatto. **`SettingsTests`**: esportazione dalle Impostazioni, importazione altrove, applicata al riavvio |
| 3 | Modifiche XMP fatte in Bridge importate come manuali | ✅ Superato (exiftool) · 🔍 verifica in Bridge a mano | **`ExternalXmpSyncTests`** (scritto prima del codice, CI). Dopo la scrittura di PhotoFlow, exiftool cambia rating, etichetta, keyword e descrizione come farebbe Bridge, e la sincronizzazione:<br>• importa rating 5 e etichetta Green;<br>• fa della keyword aggiunta un tag manuale;<br>• sopprime il tag automatico rimosso;<br>• non rimuove il tag manuale (torna nel file);<br>• fa della descrizione la didascalia dell'utente.<br>In conflitto vince la modifica più recente, con log; un file non toccato non viene riletto. Regioni nominate altrove importate come persone. La verifica in Bridge è il punto 3 qui sotto |

Test: 419, di cui 42 locali (parità, qualità, prestazioni) saltati senza `PHOTOFLOW_TESTDATA`; gli altri girano in CI su Windows e Linux. In locale (CUDA): `CaptionParityTests` superato su CPU e GPU; `Phase5PerformanceTests` (misure sotto).

## Cosa è stato fatto (tappe 5.0-5.8)

| Tappa | Contenuto |
|---|---|
| 5.0 | Test prima del codice: cambio modello volti, sincronizzazione esterna, I5 per le didascalie, esportazione/importazione |
| 5.1 | **Didascalie C** (F5-1): Florence-2 base-ft con adattatori nostri. Componenti: encoder visivo, embedding del prompt, encoder, decoder con cache, ricerca a fascio come transformers, decodifica BPE a byte. Parità esatta con PyTorch: **24/24 didascalie** identiche su CPU e CUDA. Tabella `Captions` (migrazione 0015), ricerca sulle didascalie, `dc:description` facoltativa, didascalia modificabile in Consultazione (D), livello C nell'import |
| 5.2 | Livello D rinviato (F5-2), visibile e disattivato con il motivo |
| 5.3 | Scelta dei modelli (rilevatore, volti) tra quelli installati, con ricarica senza riavvio quando nessun job è in corso |
| 5.4 | **Rigenerazione** (job `Reanalyze`). Perimetro: libreria e cartella, job, risultati di ricerca. Parti scelte, "solo modelli più vecchi" o "forza tutto", esclusione delle foto revisionate, prova su campione. Area di attesa (migrazione 0017), anteprima delle differenze, applica o scarta. Volti: trasferimento delle assegnazioni (F5-3) |
| 5.5 | **`.pfcatalog`** (F5-4) dalle Impostazioni e dalla schermata Librerie vuota: impronte con password d'esportazione o escluse, rimappatura per libreria, applicazione al riavvio. Ricalcolo delle impronte proposto in Persone quando un catalogo arriva senza |
| 5.6 | **Sincronizzazione XMP** (F5-5, migrazione 0016): stato e contenuto del file dopo ogni scrittura; controllo all'avvio, prima di ogni scrittura e a richiesta; log; importazione delle persone nominate altrove |
| 5.7 | Pagina Rigenerazione con le sue entrate (Librerie, Job, Consultazione, Impostazioni › Modelli "Usa questo modello"); controlli in Impostazioni › Catalogo e › Generali › XMP |
| 5.8 | Misure (sotto) |

## Misure

**Set locale** `PHOTOFLOW_TESTDATA/photos` (230 foto), Release, RTX 3090, CUDA, 4 analisi in parallelo (background), `Phase5PerformanceTests`:

| Misura | Valore |
|---|---|
| Didascalia Florence-2 (base-ft, fascio 3) | 0,9 s per foto su GPU, 2,5 s su CPU (`CaptionParityTests`); nell'analisi con 4 lavori: **+644 ms per foto** |
| Analisi con buffalo\_l, riconoscimento e raggruppamento | 148 s; 180 volti, 38 persone |
| Rigenerazione con YuNet + SFace (culling e volti) | 144 s; anteprima: 151 assegnazioni trasferite, 84 volti nuovi, 60 automatici non ritrovati, 6 dell'utente conservati |
| Dopo la conferma | 233 volti, 42 persone (4 gruppi nuovi), **nessuna persona senza volti**, 38 assegnazioni dell'utente, 220 impronte SFace |
| Esportazione del catalogo | 1,1 s, 1,9 MB (230 foto, 258 impronte); importazione preparata in 1,2 s |

Le didascalie di esempio sono nel report locale `PhotoFlowTestData\lab\performance\phase5-captions-*.md`.

## Decisioni tecniche prese durante la fase

Registrate in `docs/progress.md`, nel piano e nella specifica:

- **F5-1** Florence-2 base-ft con adattatori nostri. L'encoder visivo della conversione `onnx-community` non corrisponde al modello originale: coseno 0,94, didascalie diverse. Lo esportiamo noi da `florence-community/Florence-2-base-ft` (MIT) con `tools/lab/export_florence_vision.py` (Δ 4,7·10⁻⁴). Il file va pubblicato come release del repository: vedi sotto.
- **F5-2** Livello D (VLM) rinviato: Qwen2.5-VL-3B ha licenza non commerciale ("qwen-research"); il 7B richiederebbe un nostro export e ONNX Runtime GenAI, un secondo runtime fuori da `IInferenceProvider` e senza test di parità. **Decisione da confermare.**
- **F5-3** Trasferimento delle assegnazioni dei volti per sovrapposizione (IoU ≥ 0,5, migliori coppie per prime); volti dell'utente non ritrovati conservati; persone mai cancellate.
- **F5-4** `.pfcatalog` con impronte ricifrate da una chiave protetta dalla password d'esportazione, oppure escluse; importazione applicata al prossimo avvio come un ripristino.
- **F5-5** Modifiche esterne riconosciute dallo stato del file XMP e confrontate con il suo contenuto dopo l'ultima scrittura.
- **Corretti strada facendo**:
  - un tag soppresso con la versione vecchia del modello faceva sembrare la foto sempre "da rigenerare";
  - un test dipendeva dalla lingua del sistema.

## Da fare per te

1. **Release del modello Florence-2**. Il file `vision_encoder_pf.onnx` (366 MB, SHA-256 `2c540dba…`) va pubblicato nella release `models-florence2-base-ft-1` del repository: è un'azione pubblica, non l'ho fatta io. Il file è in `E:\AI\PhotoFlowTestData\models\florence2-base-ft\`. Dal terminale:

   ```bash
   gh release create models-florence2-base-ft-1 "E:/AI/PhotoFlowTestData/models/florence2-base-ft/vision_encoder_pf.onnx" --title "Florence-2 base-ft vision encoder (PhotoFlow export)" --notes "Exported by tools/lab/export_florence_vision.py from florence-community/Florence-2-base-ft@0b03b6f (MIT)."
   ```

   Nel frattempo: Impostazioni › Modelli › Importa pacchetto offline, cartella `E:\AI\PhotoFlowTestData\models` (o una con `florence2-base-ft\` dentro).
2. **Decisione F5-2** (livello D rinviato): confermala o chiedimi un'alternativa.
3. **Bridge**. Su una foto già confermata in PhotoFlow cambia, in Bridge, stelle, etichetta, una parola chiave (aggiungi e togli) e la descrizione. Poi in PhotoFlow: Impostazioni › Generali › XMP › Sincronizza ora. Verifica in Consultazione stelle, tag e didascalia. Una parola chiave che avevi aggiunto tu in PhotoFlow deve restare.
4. **Esportazione su un altro PC**. Esporta il catalogo con la password delle impronte, importalo sull'altro PC rimappando le cartelle, riavvia. Verifica Persone (riconoscimento sulle nuove foto) e la scrittura degli XMP nella nuova posizione.
5. **Rigenerazione**. In Impostazioni › Modelli prova "Usa questo modello" su YuNet (se installato), poi "Prova su campione", guarda le differenze e scarta o applica.
6. Restano validi i punti manuali delle fasi precedenti: licenze, Z5 II, set etichettato, gara vera con elenco iscritti.

## Limiti noti e rinvii (backlog §17)

- **Didascalie lente sui grandi archivi**: circa 10 ore per 57.000 foto in background. Per questo sono facoltative; è possibile un'accelerazione con la cache sulla GPU.
- **Livello D**: rinviato (F5-2).
- **Scambio dell'indice embedding**: inutile finché c'è un solo modello di embedding.
- **Registro della sincronizzazione XMP**: oggi è nel catalogo e nel riepilogo delle Impostazioni, non ancora consultabile nell'interfaccia.
