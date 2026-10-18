# Piano della fase 5 — Rigenerazione, didascalie, esportazione del catalogo, sincronizzazione XMP

Stato: **completata** (13 ott 2026; report in `docs/reports/fase-5.md`). Eseguita in autonomia su indicazione dell'utente del 2026-10-12. Branch: `phase/5-regeneration`.

Riferimenti: spec §4 (sincronizzazione con modifiche esterne), §5 (rigenerazione, modelli, livelli C/D), §7 (volti, protezione delle impronte), §8 (didascalie), §9 (esportazione/importazione, tabelle), §10 (invarianti), §12bis (tavole 5b2, 5f-5j), §13 (fase 5); D14, D21; P3, P13, P16.

## Contenuto (spec §13)

Rigenerazione completa con anteprima delle differenze; didascalie (livelli C/D); esportazione e importazione del catalogo; sincronizzazione con le modifiche XMP fatte da altri programmi.

## Criteri di accettazione e come verranno dimostrati

| Criterio (§13) | Evidenza prevista |
|---|---|
| Cambio modello volti: assegnazioni trasferite, nessuna persona persa | Test scritto prima del codice: catalogo con persone (assegnazioni automatiche e manuali); passaggio buffalo\_l → YuNet/SFace (e ritorno) con rilevatori simulati che spostano un po' i riquadri. Le assegnazioni passano ai nuovi volti per sovrapposizione, ogni persona conserva i suoi volti, i manuali restano, i prototipi vengono rifatti per il nuovo modello. Più il test d'integrazione del job con l'anteprima delle differenze |
| Esportazione e importazione su altro percorso con rimappatura riuscita | Test: catalogo con librerie, foto, tag, persone, decisioni e didascalie → `.pfcatalog` → importazione in un altro profilo con le librerie spostate in un'altra cartella. Contenuti identici, percorsi rimappati, XMP scrivibili nella nuova posizione. Impronte: con password d'esportazione si ricifrano con la chiave locale; senza password vengono tolte e ricalcolate |
| Modifiche XMP fatte in Bridge importate come manuali | Test scritto prima del codice: XMP modificato con exiftool come farebbe Bridge (rating, etichetta, keyword aggiunta e tolta, descrizione). La sincronizzazione importa le modifiche come manuali; su un tag automatico vince la versione esterna; su rating ed etichetta vince la più recente, con log; i tag manuali del catalogo non vengono mai rimossi. Verifica manuale in Bridge nel report |

## Tappe

| Tappa | Contenuto | Progetti |
|---|---|---|
| 5.0 | Test prima del codice: cambio modello volti senza perdite; sincronizzazione esterna (manuali, conflitti); I5 per le didascalie (una didascalia scritta dall'utente non viene mai sostituita) | Tests |
| 5.1 | Didascalie C (F5-1): Florence-2 base-ft ONNX con adattatori nostri su `IInferenceProvider` (encoder visivo, embedding dei token, encoder, decoder con cache), ricerca a fascio 3 e nessun trigramma ripetuto come l'originale. Parità con transformers (PyTorch) nel laboratorio (`tools/lab/florence_refs.py`). Tabella `Captions` con origine (automatica/manuale) e versione del modello; ricerca sulle didascalie; `dc:description` facoltativa (impostazione, spenta per default); didascalia visibile e modificabile in Consultazione | Tagging, Data, App, Tests |
| 5.2 | Livello D (F5-2): non incluso nella fase 5 (motivi sotto), resta disattivato con la spiegazione; nel backlog | — |
| 5.3 | Scelta dei modelli: per funzione, tra i modelli installati con adattatore (rilevatore S/M, volti buffalo\_l o YuNet/SFace, didascalie base/large). La scelta si salva nelle impostazioni; i componenti si ricaricano senza riavvio quando nessun job usa i modelli | App, Inference |
| 5.4 | Rigenerazione (job `Reanalyze`, D14): perimetro (libreria, cartella, job, ricerca, foto), componenti selezionabili (culling, embedding, volti, tag, didascalie, OCR), modalità "solo modello più vecchio" (default) o "forza tutto", esclusione dei job già revisionati. I risultati vanno in un'area di attesa e si applicano solo alla conferma; prima si può fare una prova su un campione. Anteprima delle differenze: conteggi ed esempi. Volti: associazione per sovrapposizione dei riquadri e trasferimento delle assegnazioni (F5-3). Embedding: il vecchio indice resta attivo fino allo scambio finale. Protezioni I5 | Sources, Analysis, Faces, Data, App |
| 5.5 | Esportazione/importazione `.pfcatalog` (F5-4): zip con manifest, copia coerente del catalogo, profili personalizzati, miniature facoltative. Impronte cifrate con una password d'esportazione oppure escluse. Importazione con rimappatura delle radici delle librerie, applicata al riavvio come il ripristino; il catalogo sostituito si conserva | Data, App |
| 5.6 | Sincronizzazione XMP esterna (F5-5): impronta del file XMP dopo ogni scrittura; controllo all'avvio (in background), prima di ogni scrittura e a richiesta. Lettura con exiftool di rating, etichetta, keyword, gerarchia, descrizione e regioni → modifiche come manuali, con le regole di §4 e un log. Le regioni nominate da altri programmi si possono importare nella galleria persone | Metadata, App, Data |
| 5.7 | Interfaccia: Impostazioni › Modelli "Cambia modello" → prova su campione → anteprima → applica; pagina Rigenerazione (da Librerie e Job); Impostazioni › Catalogo esporta/importa; Librerie vuota "Importa catalogo"; Impostazioni › XMP sincronizza ora e `dc:description`; livello C nell'import | App |
| 5.8 | Misure e report | Tests |

## Decisioni tecniche prese in autonomia (da registrare in `docs/progress.md` e nella specifica)

| # | Decisione | Motivo |
|---|---|---|
| F5-1 | Florence-2 **base-ft** fp32 con adattatori nostri invece di Florence2-Sharp; compito "didascalia dettagliata". Encoder del testo e decoder da `onnx-community` (verificati: Δ 2·10⁻⁴); **encoder visivo esportato da noi** dall'originale (quello della conversione dà coseno 0,94), pubblicato come release del repository | Florence2-Sharp userebbe sessioni ONNX proprie, fuori da `IInferenceProvider` (CLAUDE.md §3), come già per l'OCR (F4-1); parità esatta (24/24 didascalie) solo con il nostro encoder visivo |
| F5-2 | Livello D (VLM) rinviato al backlog | Qwen2.5-VL-3B ha licenza "qwen-research" (non commerciale; scheda Hugging Face verificata il 13 ott 2026). Il 7B è Apache-2.0 ma richiede ~5 GB, un nostro export con il Model Builder e un secondo runtime (ONNX Runtime GenAI) fuori da `IInferenceProvider`, senza test di parità praticabili. Il livello resta visibile e disattivato con il motivo |
| F5-3 | Trasferimento delle assegnazioni dei volti: coppie vecchio/nuovo volto per IoU ≥ 0,5 (accoppiamento migliore per primo). I volti manuali non associati restano; i nuovi volti non associati vanno al riconoscimento normale; prototipi rifatti per il modello nuovo dai volti assegnati | Spec §5 "associazione ai vecchi volti per sovrapposizione dei riquadri" |
| F5-4 | `.pfcatalog` = zip con `manifest.json`, `catalog.pfdb` (copia con la backup API di SQLite), `profiles/`, `thumbnails/` facoltative. Impronte ricifrate con una chiave derivata dalla password d'esportazione (PBKDF2-SHA256 + AES-GCM come la chiave locale). L'importazione si applica al riavvio | Un file unico (spec §9); nessuna dipendenza nuova |
| F5-5 | Modifiche esterne riconosciute dall'impronta del file XMP (dimensione, data di modifica, xxHash3 del contenuto) salvata dopo ogni scrittura di PhotoFlow | Oggi `XmpSync` ricorda solo le modifiche inviate, non il file |

## Rischi

| Rischio | Mitigazione |
|---|---|
| Decoder di Florence-2 con cache e ricerca a fascio in C#: complessità | Riferimenti token per token dal laboratorio; parità esatta del testo richiesta su un set di foto |
| Rigenerazione di grandi archivi: spazio dell'area di attesa | Area di attesa compatta (JSON per foto e componente), svuotata all'applicazione o all'annullamento |
| Importazione di un catalogo che sostituisce quello attuale | Mai a caldo: si applica al riavvio, con backup del catalogo corrente in `backup/replaced-*` (mai cancellato) |
| Bridge scrive XMP in modi diversi (sidecar o incorporato, `xmp:Rating`, `photoshop:` e `lr:`) | Lettura con exiftool degli stessi campi che scriviamo; test con exiftool; verifica manuale in Bridge |
