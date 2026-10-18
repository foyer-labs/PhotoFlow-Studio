# PhotoFlow — Specifica di progetto

Oct 12, 2026 · @luca

## 1. Scopo e ambito

PhotoFlow (nome provvisorio) è un applicativo desktop C#/Avalonia che importa, seleziona, tagga e rende ricercabili foto con modelli AI locali, senza cloud. Uso personale, codice destinato alla pubblicazione open source (Apache 2.0).

### Obiettivi

| # | Obiettivo |
| --- | --- |
| O1 | Import da scheda SD con copia verificata, rinomina e backup su più destinazioni |
| O2 | Culling automatico: scarti tecnici, scelta della migliore in ogni raffica |
| O3 | Tagging automatico in italiano e inglese, con keyword gerarchiche |
| O4 | Riconoscimento volti con raggruppamento "Persona N" e nomi assegnati dall'utente |
| O5 | Numeri di gara e piloti in ambito motorsport (OCR + elenco iscritti) |
| O6 | Analisi in background di archivi esistenti (stima 50.000-60.000 foto, formati misti) |
| O7 | Consultazione: ricerca per tag, persona, data, evento, EXIF, linguaggio naturale, duplicati |
| O8 | Interoperabilità tramite XMP standard, senza formati proprietari chiusi |

### Fuori ambito

| Elemento | Motivo |
| --- | --- |
| Sviluppo RAW e fotoritocco | Resta a Photoshop o ad altro software |
| Analisi dei video | I video vengono copiati, non analizzati |
| Cloud, sincronizzazione online, telemetria | Tutto locale |
| Mappa GPS | Rinviato |
| Album, modalità portatile, panorami automatici | Backlog post-stabilità (sezione 17) |

### Principi guida

| Principio | Significato |
| --- | --- |
| Nessuna perdita | Nessuna cancellazione dalla sorgente, RAW mai modificati, nessuna eliminazione automatica |
| Le azioni dell'utente prevalgono | Decisioni e tag manuali, ovunque fatti, vincono sempre su quelli automatici |
| Il catalogo è la fonte primaria, XMP lo strato di interoperabilità | Dati ricchi nel catalogo, sempre esportati anche in formato standard |
| Generico, niente cablato | Hardware, percorsi, modelli, profili sono configurazione |
| Incertezza → revisione | Il sistema non decide da solo quando non è sicuro |
| Controllo delle modifiche | Ogni modifica a decisioni già prese passa da questo documento prima del codice |

## 2. Registro delle decisioni

Tutte le decisioni sotto sono confermate; ogni modifica richiede una valutazione d'impatto e l'aggiornamento di questo documento.

| # | Tema | Decisione | Motivazione |
| --- | --- | --- | --- |
| D1 | Linguaggio | C#/.NET puro con ONNX Runtime; Python solo come strumento di laboratorio (export, riferimenti di parità, benchmark) | Tutti i modelli necessari esistono in ONNX; distribuzione semplice; rilevamento hardware nativo |
| D2 | Interfaccia | Avalonia | Multipiattaforma (Windows prioritario, Mac/Linux possibili) |
| D3 | Ambito funzionale | Elaborazione + consultazione (opzione B) | Senza consultazione il valore resta chiuso nel catalogo; ricerca in linguaggio naturale |
| D4 | Sorgenti | SD, cartelle e archivi esistenti; formati RAW, JPG, PNG, WebP, TIFF, HEIC | Uso anche su archivi non NEF |
| D5 | Catalogo | Posizione configurabile: locale (default) o rete con protezioni; esportazione/importazione | Flessibilità senza rischi di corruzione non gestiti |
| D6 | Album | Rinviati al backlog | Costo basso se aggiunti dopo |
| D7 | Culling | Logica conservativa: scarto solo su segnale certo, incertezza → "da rivedere" | Uno scarto sbagliato costa più di uno mancato |
| D8 | Scelta del profilo | Manuale + suggerimento (C), con modalità automatica attivabile per job | Prevedibilità con aiuto nei casi misti |
| D9 | Lingua keyword XMP | Italiano e inglese (piatte); gerarchiche solo in italiano | Compatibilità e ricerca in entrambe le lingue |
| D10 | Lingua interfaccia | Italiano e inglese, configurabile (default: lingua di sistema) | — |
| D11 | Volti | Previsti da subito, configurabili (`Off` / `DetectOnly` / `Recognize`) | Servono già per gli occhi chiusi |
| D12 | Protezione volti | Cifratura delle sole impronte (C), chiave legata all'account o a password | Protegge il dato biometrico; perdita della chiave = ricalcolo, non perdita di dati |
| D13 | Sincronizzazione XMP | Modifiche esterne importate come manuali; tag manuali mai rimossi | Le azioni dell'utente prevalgono |
| D14 | Rigenerazione | Rianalisi selettiva con anteprima delle differenze e ritorno indietro | Aggiornare i modelli senza perdere il lavoro manuale |
| D15 | Rilevatore oggetti | D-FINE (S su CPU, M su GPU), RF-DETR come alternativa; YOLO Ultralytics escluso | Licenza Apache 2.0; nessun vincolo AGPL |
| D16 | Selezione modelli | Modello e taglia selezionabili manualmente per ogni funzione | Controllo e sperimentazione |
| D17 | Licenza progetto | Apache 2.0 | Coerente con le dipendenze, clausola brevetti |
| D18 | Equipaggi motorsport | Solo numero di vettura (+ team) nell'XMP; equipaggio associato nel catalogo | Nessun nome scritto se non certo |
| D19 | Modalità portatile | Backlog post-stabilità, con percorsi centralizzati in `IAppPaths` fin da subito | Riduce i tempi di build durante lo sviluppo |
| D20 | Avvio prima del set etichettato | Modalità prudente: scarti scritti solo come "da rivedere" | Culling non ancora validato |
| D21 | Politica XMP predefinita | EmbeddedForNonRaw per le librerie create da un import (incorporato in JPG/TIFF/PNG, sidecar per i RAW); SidecarOnly per le librerie create da un archivio esistente, con passaggio a EmbeddedForNonRaw solo su conferma esplicita che indica il numero di file coinvolti (aggiornata il 12 ott 2026) | Il visualizzatore di Windows ignora i sidecar; Photoshop/Camera Raw legge gli incorporati nei JPG e i sidecar nei RAW; gli originali di un archivio non si riscrivono senza consenso |
| D22 | Piano di sviluppo | Fasi 0-5 (sezione 13), con fase 0 di setup e laboratorio e fase 5 per rigenerazione completa, didascalie, esportazione catalogo e sincronizzazione esterna | Distribuisce tutte le funzioni definite in fasi chiudibili e utilizzabili |
| D23 | Passaggio a modalità normale | Si sblocca solo quando tutte e tre le metriche del culling sono entro obiettivo (intervallo di confidenza incluso) | Confermata in Design: evita di passare a rating -1 con metriche ancora incerte |
| D24 | Decisioni di Design | P1-P20 (sezione 17) confermate e vincolanti per l'implementazione | Emerse durante la progettazione delle schermate, coerenti con i principi della sezione 1 |

## 3. Architettura

Soluzione .NET a progetti separati: `Core` contiene dominio, interfacce e orchestrazione senza dipendenze esterne; tutto il resto si collega tramite interfacce.

### Struttura della soluzione

| Progetto | Responsabilità | Dipendenze principali |
| --- | --- | --- |
| `PhotoFlow.Core` | Entità, interfacce, orchestrazione della pipeline | Nessuna |
| `PhotoFlow.Sources` | Sorgenti (SD, cartella), rilevamento SD, ingest, copia verificata, rinomina | System.IO.Hashing (xxHash3) |
| `PhotoFlow.Imaging` | Gestori di formato (`IImageHandler`): anteprime, decodifica | exiftool, SkiaSharp, Magick.NET (include LibRaw 0.22.x: decodifica RAW di riserva, F1-3) |
| `PhotoFlow.Metadata` | Lettura EXIF, scrittura/lettura XMP, sincronizzazione | exiftool (`-stay_open`) |
| `PhotoFlow.Inference` | Selezione hardware, sessioni ONNX, registro modelli | ONNX Runtime, ONNX Runtime GenAI |
| `PhotoFlow.Analysis` | Analizzatori del culling, raggruppamenti, decisioni | Nessuna: elaborazioni in C# verificate contro OpenCV nel laboratorio (F1-4; OpenCvSharp non ha runtime macOS aggiornati) |
| `PhotoFlow.Faces` | Impronte, confronto, clustering, cifratura impronte | HdbscanSharp |
| `PhotoFlow.Tagging` | CLIP zero-shot, RAM++, Florence-2, VLM, OCR, vocabolario | Florence2-Sharp, RapidOcrNet |
| `PhotoFlow.Data` | Catalogo, migrazioni, indice di ricerca (`ISearchIndex`) | Microsoft.Data.Sqlite, Dapper |
| `PhotoFlow.App` | Interfaccia MVVM, localizzazione IT/EN | Avalonia, CommunityToolkit.Mvvm |
| `PhotoFlow.Tests` | Unitari, parità, integrazione, scenari | xUnit |

### Pipeline

Le fasi per foto sono collegate da `System.Threading.Channels` limitati e partono mentre la copia è in corso; le barriere di lotto attendono l'intero insieme.

| # | Fase | Tipo | Note |
| --- | --- | --- | --- |
| 1 | Copia + verifica | Per foto | Saltata nei job `AnalyzeInPlace`; nessuna fase tocca un file non verificato |
| 2 | Estrazione anteprima | Per foto | Gestore scelto dal contenuto del file, non dall'estensione |
| 3 | Analizzatori del culling | Per foto, in parallelo | Esposizione, nitidezza, soggetto, volti/occhi, embedding |
| 4 | Raggruppamenti | Barriera di lotto | Raffiche, bracketing, timelapse, duplicati |
| 5 | Decisione proposta | Per gruppo | Sezione 6 |
| 6 | Tagging e OCR | Per gruppo | Solo foto tenute; il rappresentante propaga ai membri |
| 7 | Volti | Barriera di lotto | Confronto con i noti, poi clustering a blocchi |
| 8 | Revisione | Interfaccia | Conferma dell'utente |
| 9 | Scrittura XMP | Per foto | Solo dopo conferma, secondo la politica della libreria |

Tipi di job: `Import` (fasi 1-9), `AnalyzeInPlace` (2-9), `Reanalyze` (fasi selezionate, sezione 5).

### Interfacce chiave

```csharp
public interface ISource { IAsyncEnumerable<SourceItem> EnumerateAsync(CancellationToken ct); }
public interface IImageHandler { bool CanHandle(FileSignature sig); Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct); }
public interface IPhotoAnalyzer { string Id { get; } Task<AnalyzerResult> AnalyzeAsync(PhotoContext photo, SceneProfile profile, CancellationToken ct); }
public interface IBatchStage { Task RunAsync(JobContext job, IProgress<StageProgress> progress, CancellationToken ct); }
public interface ITagger { TaggingLevel Level { get; } Task<IReadOnlyList<TagResult>> TagAsync(PhotoContext photo, CancellationToken ct); }
public interface IInferenceProvider { HardwareInfo Detected { get; } Task<IInferenceSession> CreateSessionAsync(ModelDescriptor model, ExecutionTarget target, CancellationToken ct); }
public interface IInferenceSession : IDisposable { Task<IReadOnlyList<NamedTensor>> RunAsync(IReadOnlyList<NamedTensor> inputs, CancellationToken ct); }
public interface IMetadataWriter { Task<MetadataWriteResult> WriteAsync(MetadataTarget target, MetadataChanges changes, WritePolicy policy, CancellationToken ct); } // MetadataTarget: percorso + RAW sì/no (Asset non contiene il percorso)
public interface IObjectDetector { Task<IReadOnlyList<Detection>> DetectAsync(RgbImage image, CancellationToken ct); }
public interface IImageOperations { /* decodifica e ridimensionamento: isola SkiaSharp */ }
public interface ISearchIndex { /* FTS e ricerca vettoriale, isolati per un futuro PostgreSQL */ }
public interface IAppPaths { /* unico punto di risoluzione dei percorsi: abilita la modalità portatile */ }
```

Inferenza senza dipendenze in `Core` (decisione del 12 ott 2026): `NamedTensor` (nome, forma, dati `ReadOnlyMemory<T>`) ed `ExecutionTarget` (Auto/CPU/GPU) sono tipi di `Core`; solo `PhotoFlow.Inference` dipende da ONNX Runtime e converte i tensori senza copie. Gli adattatori dei modelli vivono nei progetti di dominio (`.Analysis`, `.Faces`, `.Tagging`) ed espongono interfacce per funzione (es. `IObjectDetector`, `IFaceAnalyzer`, `IImageEmbedder`) consumate dagli analizzatori. `Dispose` della sessione libera la memoria del modello (caricamento fase per fase).

### Moduli trasversali

| Modulo | Scelta |
| --- | --- |
| Hardware | Prova CUDA → CoreML → CPU; micro-benchmark al primo avvio |
| VRAM | Modelli caricati e scaricati fase per fase (funziona con GPU da 4-6 GB) |
| Registro modelli | Manifest JSON: URL, SHA-256, dimensione, licenza, adattatore; download al primo uso |
| Profili | File JSON versionati |
| Percorsi | Tutti tramite `IAppPaths` |
| Localizzazione | File di risorse IT/EN separati dal codice |

## 4. Sorgenti, formati e metadati

L'import da SD è un caso particolare dell'analisi di una raccolta; i metadati si scrivono in XMP standard, unendo e mai sovrascrivendo.

### Ingest

| Funzione | Specifica |
| --- | --- |
| Rilevamento SD | Polling \~2 s dei volumi rimovibili con cartella `DCIM` (multipiattaforma) |
| Template percorsi | Configurabile, es. `{yyyy}/{yyyy-MM-dd}_{evento}/{yyyyMMdd}_{evento}_{seq:0000}.{ext}` |
| Copia | Una sola lettura della sorgente, scrittura parallela sulle destinazioni locali, hash calcolato in lettura |
| Verifica | Rilettura della destinazione e confronto xxHash3 |
| NAS | Destinazione secondaria asincrona con retry |
| Già importate | Riconosciute tramite hash + data di scatto: nessun duplicato al secondo import |
| RAW+JPEG | Trattati come una sola foto (`AssetPairs`) |
| Video | Copiati, non analizzati |

### Gestori di formato

| Formato | Anteprima | Libreria | Note |
| --- | --- | --- | --- |
| RAW (NEF, CR2/CR3, ARW, RAF, DNG…) | Anteprima incorporata | exiftool | Fallback: decodifica completa con Magick.NET (LibRaw) se l'anteprima manca o è piccola; NEF High Efficiency: solo anteprima (R15) |
| JPG, PNG, WebP | Decodifica diretta | SkiaSharp | Percorso veloce |
| TIFF | Decodifica con riduzione | Magick.NET | 16 bit, multipagina, file grandi |
| HEIC | Decodifica | Magick.NET (libheif) | Foto da smartphone |

### EXIF letti

`SubSecDateTimeOriginal` (ordine delle raffiche), ISO, tempo, diaframma, focale, obiettivo, compensazione dell'esposizione (bracketing), punto AF Nikon se disponibile (zona di nitidezza). La fonte della data (EXIF/file/sconosciuta) viene registrata.

### Politiche di scrittura (per libreria)

| Politica | Comportamento | Uso |
| --- | --- | --- |
| `SidecarOnly` | Sempre `.xmp` accanto all'originale | Originali intoccabili |
| `EmbeddedForNonRaw` | Incorporato in JPG/TIFF/PNG, sidecar per i RAW | Visibilità in Esplora risorse e Photoshop; copia `_original` configurabile |
| `CatalogOnly` | Solo catalogo, esportabile | Archivi in sola lettura |

Default per le librerie create da un import: `EmbeddedForNonRaw`, perché l'utente consulta con il visualizzatore di Windows (che ignora i sidecar) e modifica con Photoshop/Camera Raw. Default per le librerie create da un archivio esistente (`AnalyzeInPlace`): `SidecarOnly`; il passaggio a `EmbeddedForNonRaw` richiede conferma esplicita con il numero di file coinvolti e la copia `_original` attiva (D21).

### Campi XMP

| Dato | Campo |
| --- | --- |
| Scarto | `xmp:Rating = -1` (in modalità prudente: etichetta rossa) |
| Da rivedere | `xmp:Label = "Red"` |
| Keyword piatte | `dc:subject`, italiano + inglese |
| Keyword gerarchiche | `lr:hierarchicalSubject`, solo italiano |
| Volti | Regioni MWG con nome |
| Didascalia | `dc:description` (opzionale) |

Nomi di persone, eventi e piloti non si traducono. Tag "non rivisti": solo inglese.

Tag (fase 3, 13 ott 2026): `dc:subject` riceve la chiave inglese di ogni tag attivo e l'etichetta italiana una volta rivista; `lr:hierarchicalSubject` riceve `Categoria|Etichetta` (italiana se rivista, altrimenti inglese); l'evento del job è un tag (`Eventi|Nome`); la lista nera non si scrive mai. `XmpSync` ricorda le keyword scritte da PhotoFlow: un tag tolto più tardi (soppresso, in lista nera, non più trovato) esce dal file, mentre le keyword scritte da altri programmi o dall'utente non vengono mai toccate.

Scrittura in fase 1 (12 ott 2026), solo dopo la conferma in Revisione e con la politica della libreria: le stelle dell'utente diventano `xmp:Rating`; uno scarto `xmp:Rating = -1` (prudente: etichetta rossa); da rivedere etichetta rossa. Le foto senza nulla da dire (tenute, simili, migliori senza stelle) non vengono toccate; se erano già state scritte, rating ed etichetta vengono azzerati. Le coppie RAW+JPEG ricevono gli stessi campi (sidecar per il RAW). Una scrittura non riuscita resta in sospeso in `XmpSync` e non blocca le altre.

### Sincronizzazione con modifiche esterne

| Situazione | Comportamento |
| --- | --- |
| XMP modificato da altri software | Modifiche importate come manuali |
| Conflitto su tag automatico | Vince la versione esterna |
| Conflitto su rating/etichetta | Vince la più recente, con log |
| Tag manuali del catalogo | Mai rimossi |
| Keyword e volti già presenti | Mantenuti; regioni volto nominate da altri software importabili nella galleria persone |

Fase 5 (13 ott 2026, F5-5). Dopo ogni scrittura PhotoFlow ricorda due cose del file che contiene l'XMP: lo stato (sidecar o immagine: dimensione, data, xxHash3 del contenuto) e i valori letti (rating, etichetta, keyword, gerarchia, descrizione, regioni).

- **Quando si controlla.** In background all'avvio, prima di ogni scrittura di PhotoFlow e a richiesta (Impostazioni › Generali › XMP). Un file con lo stesso stato non viene riletto.
- **Keyword.** Una keyword aggiunta altrove diventa un tag manuale. Una keyword tolta altrove sopprime il tag, se era automatico; un tag manuale resta e torna nel file alla scrittura successiva.
- **Rating ed etichetta.** Vince la modifica più recente: la data del file contro l'ultima modifica dell'utente nel catalogo (`Decisions.UserUpdatedAtMs`).
- **Descrizione.** Diventa la didascalia dell'utente.
- **Regioni volto.** Quelle nominate da altri programmi si importano su richiesta: il volto che si sovrappone (IoU ≥ 0,3) prende quella persona, creata se il nome è nuovo; le assegnazioni dell'utente non cambiano.
- **Log.** Ogni importazione e ogni conflitto restano in `XmpSyncLog`.

## 5. Modelli, inferenza e rigenerazione

Tutti i modelli girano in ONNX Runtime; ogni funzione ha un modello attivo selezionabile manualmente (modello e taglia), con default scelto in base all'hardware.

### Modelli per funzione

| Funzione | Default | Alternative | Peso indicativo | Disponibilità ONNX |
| --- | --- | --- | --- | --- |
| Rilevamento oggetti | D-FINE S (CPU) / M (GPU), pesi `obj2coco` fp32 | RF-DETR Nano-Large | 10-19M parametri | Conversioni `onnx-community` fissate per commit + SHA-256, verificate con test di parità contro il nostro export (`tools/lab`); riserva: nostro export su GitHub Releases |
| Volti (rilevamento, landmark, impronte) | InsightFace buffalo\_l | buffalo\_s; YuNet/SFace (Apache 2.0); impronte AdaFace IR-101 opzionali (P22, non commerciale, convertito sul PC) | 326 MB (AdaFace +249 MB) | Nativo |
| Embedding (raffiche, ricerca, duplicati, zero-shot) | CLIP ViT-B/32 fp32 (F2-1) | — | 606 MB (immagine 352 MB + testo 254 MB) | Disponibile (Xenova, fissato per commit + SHA-256); la conversione quantizzata è esclusa (coseno 0,87-0,97 con l'originale) |
| Tagger | RAM++ fp32 (F3-1) | — | 1,86 GB | Export `CannotFindObject/RAM_ONNX` fissato per commit + SHA-256 (mirror `imagent/ram-plus-onnx`); stessi tag dell'originale su 52/52 foto; la int8 è esclusa |
| Didascalie | Florence-2 base-ft fp32 (F5-1) | Florence-2 large (da adattare) | 1,09 GB | Encoder del testo, decoder e tokenizer `onnx-community` fissati per commit; encoder visivo esportato da noi (`tools/lab/export_florence_vision.py`) perché quello della conversione non corrisponde all'originale (coseno 0,94); adattatori nostri su `IInferenceProvider`, parità esatta delle didascalie con PyTorch su CPU e CUDA |
| VLM (livello D) | **Qwen3.5-9B** int4 (U1 rivista il 15 ott 2026 su richiesta dell'utente: multimodale nativo, Apache-2.0, migliore su testi nelle foto e dettagli, inventa meno marchi; tabella di embedding int8, picco GPU 9,8 GB). Qwen2.5-VL-7B-Instruct (fase 5b) resta in uso dove è già convertito finché l'utente non sceglie il nuovo in Impostazioni › Modelli: un modello non cambia mai da solo | — | ~6 GB convertito (18 GB di pesi originali; Qwen2.5-VL ~6 e 16,6 GB) | Pesi originali scaricati dall'utente e convertiti dall'app con il Model Builder di ONNX Runtime GenAI (U2, U3); esecuzione con ONNX Runtime GenAI in un processo separato (U4, eccezione a CLAUDE.md §3); il 3B è escluso (licenza "qwen-research", non commerciale) |
| OCR | PP-OCRv4 det + PP-OCRv3 rec inglese (F4-1) | PP-OCRv5 latina (rilevatore da 88 MB) | 14 MB | Modelli RapidOCR fissati per commit + SHA-256 (`SWHL/RapidOCR`), adattatori nostri su `IInferenceProvider`; parità esatta con RapidOCR Python; riconoscitore su CPU (F4-4) |
| Estetica | Predittore LAION su embedding CLIP | — | < 5 MB | Riscrivibile in poche righe |

Qwen3-VL è escluso finché ONNX Runtime GenAI non lo supporta nativamente.

Fase 2 (13 ott 2026): CLIP in C# con preparazione identica all'originale (ridimensionamento bicubico come Pillow del lato corto a 224, ritaglio centrale, normalizzazione CLIP) e tokenizzatore BPE portato in C#; parità con `openai/clip-vit-base-patch32` (PyTorch): tensore d'ingresso identico, coseno 1,0 su CPU e CUDA. L'embedding si calcola sull'anteprima già decodificata (fase 3 della pipeline) e si salva nella tabella `Embeddings` con la versione del modello.

Note dal laboratorio (fase 0, 12 ott 2026): RAM++ int8 e CLIP quantizzato non accelerano su GPU (la quantizzazione dinamica gira in CPU: RAM++ int8 528 ms/foto su RTX 3090 contro 614 su CPU), quindi su GPU vanno usate le varianti fp32/fp16. La RAM++ int8 della community non supera la parità con l'originale (stesso insieme di tag in 1 immagine su 8, Jaccard medio 0,895): in fase 3 si usa fp32 o un export verificato. D-FINE fp16 non è usabile con CUDA (GridSample solo fp32).

### Livelli di tagging

| Livello | Contenuto | Default |
| --- | --- | --- |
| A | Zero-shot CLIP sul vocabolario personale | Attivo |
| B | RAM++ | Attivo |
| C | Didascalie Florence-2 | Opzionale |
| D | Didascalie VLM | Opzionale, solo GPU |

Fase 3 (13 ott 2026): il livello A non richiede inferenza in più (usa gli embedding CLIP già salvati); il livello B (RAM++ Swin-L, 384×384: sul set locale con RTX 3090 l'analisi completa passa da 6,9 a 4,2 foto/s; su sola CPU il costo si misura con il benchmark in-app) è attivo per default solo con la GPU, altrimenti si propone A soltanto; la scelta è per job (procedura d'import) e ricordata.

### Selezione hardware

| Aspetto | Comportamento |
| --- | --- |
| Ordine di prova | CUDA → CoreML → CPU |
| Varianti di build | Windows: CUDA per GPU NVIDIA + CPU per le altre (decisione F1-2 del 12 ott 2026). Le librerie CUDA/cuDNN sono un "pacchetto GPU" scaricato al primo uso dall'archivio di ridistribuzione NVIDIA con verifica SHA-256 (importabile offline). DirectML escluso: sulla RTX 3090 produce risultati errati con D-FINE e va in crash con CLIP quantizzato (laboratorio, 12 ott 2026). Ogni backend futuro (es. Windows ML) entra solo dopo aver superato i test di parità |
| Primo avvio | Micro-benchmark su \~50 foto con stima dei tempi; su sola CPU propone un livello di tagging più leggero |
| Selezione manuale | Per funzione, con licenza, spazio, memoria stimata e ultimo benchmark mostrati; pulsante "benchmark" prima dell'attivazione |
| Compatibilità | Selezionabili solo modelli con un adattatore di input/output implementato |

### Stime di tempo (da validare con il benchmark)

| Scenario | GPU (RTX 3090) | Solo CPU (8 core) |
| --- | --- | --- |
| Import 1.000 scatti: culling + tagging A+B + volti | \~6-11 min | \~22-50 min |
| Archivio 50.000-60.000 foto: tagging A+B + volti | \~1,5-3 h | \~8-20 h |

### Rigenerazione (job `Reanalyze`)

| Aspetto | Specifica |
| --- | --- |
| Perimetro | Libreria, cartella, job, risultato di ricerca, singola foto |
| Cosa | Tag, didascalie, punteggi di culling, embedding, volti, OCR (selezionabili) |
| Modalità | "Solo modello più vecchio" (default) o "Forza tutto" |
| Protezioni | Tag manuali mai toccati; tag rimossi dall'utente restano soppressi; decisioni dell'utente mantenute; opzione per escludere job già revisionati |
| Nuovo modello volti | Nuove impronte su tutto; associazione ai vecchi volti per sovrapposizione dei riquadri; assegnazioni trasferite; clustering solo dei non associati |
| Nuovo modello embedding | Vecchio indice attivo fino al completamento del nuovo, poi scambio in un'unica operazione |
| Sicurezza | Anteprima delle differenze; risultati precedenti conservati fino a conferma; prova su campione prima dell'intero archivio |

Fase 5 (13 ott 2026). La rigenerazione è un job `Reanalyze` a bassa priorità.

- **Cosa ricalcola.** Con "solo modello più vecchio" ricalcola le parti con una versione diversa da quella dei modelli attuali: culling e volti, embedding mancanti, tag B attivi, didascalie automatiche, OCR. Con "forza tutto" ricalcola ogni parte scelta.
- **Area di attesa.** I risultati vanno in `ReanalysisStaging`: l'anteprima conta le differenze (proposte, tag, volti, didascalie, numeri) con esempi. "Applica" li scrive con le protezioni di I5, "Scarta" li elimina. La prova su campione usa 50 foto a caso del perimetro.
- **Cambio di modello dei volti (F5-3).** Avviene per sovrapposizione IoU ≥ 0,5, migliori coppie per prime. Volti dell'utente non ritrovati: restano. Persone: mai cancellate. Copertine e prototipi passano ai nuovi volti. I nuovi volti non associati vanno poi a riconoscimento e raggruppamento.
- **Scelta dei modelli.** Rilevatore (D-FINE S/M) e volti (buffalo\_l o YuNet/SFace) si scelgono tra quelli installati (Impostazioni › Modelli, "Usa questo modello"). I modelli si ricaricano subito se nessun job è in corso, poi si apre la rigenerazione con la prova su campione.
- **Scambio dell'indice embedding.** Oggi c'è un solo modello di embedding (CLIP B/32): lo scambio dell'indice non serve; resta nel backlog per un secondo modello.
- **Misure (set locale, 230 foto, RTX 3090).** Da buffalo\_l a YuNet + SFace: 38 persone, nessuna persa, le 38 assegnazioni dell'utente conservate, 151 assegnazioni automatiche trasferite, 84 volti nuovi, 60 automatici non ritrovati; 144 s.

## 6. Culling e profili di scena

Uno scarto avviene solo su un segnale ad alta affidabilità chiaramente oltre soglia; ogni incertezza produce "da rivedere".

### Raffiche

| Condizione | Regola |
| --- | --- |
| Con EXIF | Scatti entro N secondi (default 2 s) e similarità degli embedding sopra soglia |
| Senza EXIF | Solo similarità |
| Cambio di soggetto | Crollo della similarità spezza la raffica |

Fase 2 (13 ott 2026, `SequenceGrouper`): i raggruppamenti si calcolano per fotocamera (modello + numero di serie), come barriera di lotto quando tutte le foto del job sono analizzate, e si ricalcolano per intero a ogni ripresa (idempotenti, F2-4); i gruppi manuali restano. Raffica: scatti consecutivi con intervallo ≤ `maxBurstGapSeconds` del profilo (2,0 s, da `SubSecDateTimeOriginal`, tolleranza 1 ms) e similarità CLIP tra scatti consecutivi ≥ `minSimilarity` (0,90); sotto soglia la raffica si spezza. Senza data di scatto: solo similarità; senza embedding: solo tempo. Classifica (P17): punteggio composito con i pesi del profilo su componenti normalizzate nella raffica (dettaglio relativo al massimo della raffica, esposizione, occhi, soggetto relativo al massimo); la prima è "migliore", le altre valide "simili"; se le prime due distano meno di `tieMargin` (0,02) entrambe sono "da rivedere"; gli scarti tecnici restano scarti. La decisione per gruppo si rifà dai segnali salvati con il profilo di ciascuna foto, senza nuova analisi.

### Segnali per foto

| Segnale | Misura | Affidabilità |
| --- | --- | --- |
| Nitidezza | Assoluta: 1 − *blur effect* (Crété-Roffet 2007) multi-scala (1×, ½, ¼) sulla zona del profilo e sull'intero fotogramma; relativa nella raffica: varianza del Laplaciano sulla zona, dopo leggera sfocatura anti-rumore | Alta per il mosso grave |
| Esposizione | % alte luci/ombre perse nella zona del soggetto; soglie tolleranti (l'anteprima JPEG è più severa del RAW). Senza soggetto (zona = intero fotogramma) solo il fotogramma vuoto (≥ 95% bruciato o nero) | Media |
| Occhi | Apertura (rapporto altezza/larghezza dei contorni 33-42 e 87-96) dagli 106 landmark, per volti rilevanti (≥ 40% del volto più grande); sconosciuto con occhiali da sole, volto piccolo, di profilo o rivolto verso il basso (gli occhi aperti sembrano chiusi). Con YuNet/SFace (solo 5 landmark): sempre sconosciuto | Alta su volti grandi (solo buffalo\_l) |
| Soggetto | Presenza, dimensione, posizione (D-FINE) | Alta per auto e persone |
| Estetica | Predittore LAION | Bassa: solo spareggi |

### Decisione

1. Scarti tecnici: solo segnale ad alta affidabilità chiaramente oltre soglia; vicino alla soglia o incerto → "da rivedere".
2. Classifica nella raffica: punteggio composito con i pesi del profilo, normalizzato dentro la raffica.
3. Esito: la prima diventa "migliore"; se le prime due sono quasi a pari merito, entrambe "da rivedere".

| Esito | Significato | XMP |
| --- | --- | --- |
| Scarto | Problema tecnico certo | Rating -1 (modalità prudente: etichetta rossa) |
| Da rivedere | Incertezza o pari merito | Etichetta rossa |
| Migliore | Prima della raffica | Nessun rating forzato (opzionale 1 stella) |
| Simile | Valida ma non prima | Solo catalogo |
| Tenuta | Scatto singolo senza problemi | Nessuna scrittura |

Fase 1 (scatti singoli, 12 ott 2026). Scarto solo con: fotogramma vuoto; nitidezza sotto la soglia di scarto **sia** nella zona **sia** sull'intero fotogramma (lo sfondo sfocato voluto, bokeh o panning, non basta); entrambi gli occhi chiusi del volto principale con volto ≥ 150 px (scala di riferimento 2048). Da rivedere: nitidezza sotto la soglia di revisione nella zona, un occhio chiuso o occhi chiusi in altri volti rilevanti, alte luci/ombre nella zona del soggetto. Misure sull'anteprima orientata ridotta a 2048 px di lato lungo. Scelta della misura (laboratorio `blur_metrics.py`, 230 foto reali sfocate artificialmente): con la soglia sulla peggiore foto originale la varianza del Laplaciano intercetta il 5-30% del mosso (9-25 px), il *blur effect* il 61-90%; la versione multi-scala evita la saturazione con sfocature molto ampie. Soglie iniziali (da tarare sul set etichettato, §14): scarto sotto 0,35 (nessuna originale; 74% delle sfocature gaussiane σ 2,5, 64-87% del mosso 15-100 px), revisione sotto 0,45 (2% delle originali). Esito sulle 230 foto reali con il profilo Eventi: nessuno scarto, 7,4% da rivedere.

### Sequenze speciali (mai trattate come raffiche)

| Sequenza | Riconoscimento | Comportamento |
| --- | --- | --- |
| Bracketing | Compensazione variabile a scatti ravvicinati | Gruppo "bracketing", nessuno "simile" |
| Timelapse | Intervallo regolare ripetuto | Gruppo "sequenza", nessuna classifica |
| Panorama | Contrassegno manuale (automatico nel backlog) | Nessuna classifica |

Regole della fase 2 (13 ott 2026): bracketing = almeno 3 scatti ravvicinati con compensazione dell'esposizione che copre ≥ 0,3 EV, oppure (senza compensazione) EV100 calcolato da tempo, diaframma e ISO che varia di ≥ 1 EV; timelapse = almeno 10 scatti a intervallo ≥ 1 s costante entro ±10% (±1 s quando mancano i decimi di secondo). Le due regole si applicano prima delle raffiche: una sequenza riconosciuta non diventa mai raffica (test `SequenceGrouperTests`, `GroupingStageTests` con EXIF reali scritti da exiftool).

### Metriche obiettivo

| Metrica | Obiettivo |
| --- | --- |
| Scarti sbagliati | < 2% degli scarti |
| Scarti intercettati | > 70% |
| Quota "da rivedere" | < 20% |

### Apprendimento dalle correzioni

Ogni correzione è salvata in `Corrections` con i segnali dell'epoca; dopo un numero sufficiente di correzioni per profilo, l'applicativo suggerisce di spostare le soglie. Non cambia nulla da solo e non addestra modelli.

Fase 2 (13 ott 2026, `ThresholdAdvisor`): con almeno 30 correzioni su un profilo personalizzato, la sezione Profili propone la soglia di scarto per mosso che avrebbe minimizzato 3 × scarti sbagliati − scarti intercettati (gli scarti sbagliati pesano di più, come negli obiettivi), con il conteggio di cosa cambia; si applica solo con "Applica suggerimento" e salvataggio. Le altre soglie (occhi, esposizione) nel backlog.

### Profili predefiniti

Definiti in `profiles/*.json` (versionati, incorporati nell'applicazione) con zone, soglie e pesi.

Profili personalizzati (fase 2, 13 ott 2026): si creano duplicando un profilo (predefiniti in sola lettura) e si salvano nella cartella `profiles` della configurazione come `custom-<nome>.v<versione>.json`; ogni salvataggio scrive la versione successiva e conserva le precedenti; un file illeggibile viene segnalato e lasciato dov'è. Un profilo che non c'è più (file spostato) viene sostituito da Eventi invece di bloccare job e revisione. Due profili con la stessa "chiave di misura" (zone di nitidezza, classi del soggetto, volti attivi o no, soglie degli occhi) producono gli stessi segnali: passare dall'uno all'altro cambia solo la decisione, senza rianalisi.

| Profilo | Zona nitidezza | Esposizione | Volti | Soggetto | OCR |
| --- | --- | --- | --- | --- | --- |
| Eventi | Punto AF → volti | Normale | `Recognize` | Persone | No |
| Ritratto | Occhi/volto | Normale | `Recognize` | Persone | No |
| Motorsport | Solo veicolo (panning ignorato) | Tollerante | `DetectOnly` | Auto, moto | Sì |
| Animali | Animale | Normale | `DetectOnly` | Classi COCO di animali | No |
| Paesaggio | Riquadri (il più nitido) | Normale | `Off` | — | No |
| Notturna/Aurora | Relativa nella raffica, nessuno scarto assoluto | Disattivata | `Off` | — | No |
| Archivio | Culling disattivato | — | `Recognize` | — | Opzionale |

### Pesi iniziali del punteggio composito (da tarare)

| Profilo | Nitidezza | Occhi | Esposizione | Soggetto | Estetica |
| --- | --- | --- | --- | --- | --- |
| Eventi | 35 | 30 | 15 | 10 | 10 |
| Ritratto | 30 | 45 | 15 | 0 | 10 |
| Motorsport | 50 | 0 | 15 | 30 | 5 |
| Animali | 45 | 0 | 15 | 30 | 10 |
| Paesaggio | 45 | 0 | 35 | 0 | 20 |
| Notturna/Aurora | 80 | 0 | 0 | 0 | 20 |

### Scelta del profilo

| Modalità | Comportamento |
| --- | --- |
| Manuale + suggerimento (default) | Profilo principale scelto all'avvio; i gruppi che sembrano di altro tipo vengono segnalati nella revisione |
| Automatica (per job) | CLIP assegna il profilo per gruppo; profilo di riserva obbligatorio sotto soglia di confidenza, con segnalazione; profilo e confidenza mostrati nel riepilogo |
| Personalizzazione | Duplica e modifica un profilo; i predefiniti restano intatti; cambio profilo per gruppo con rianalisi |

Fase 2 (13 ott 2026): zero-shot CLIP con un insieme di frasi inglesi per scena (Archivio escluso), probabilità = softmax di 100 · coseno, media sulle foto del gruppo. Automatica: profilo assegnato con probabilità ≥ 0,5, altrimenti il profilo di riserva; manuale: suggerimento nella revisione con probabilità ≥ 0,6 e diverso dal profilo del job. Le foto analizzate con un profilo diverso da quello assegnato si rianalizzano in parallelo (solo se la chiave di misura cambia); la scelta dell'utente per un gruppo non viene mai sostituita (I5). Misura sul set locale (230 foto, CUDA): la modalità automatica aggiunge ~55% al tempo d'analisi (52 s contro 33 s). Classificare la scena per foto prima dell'analisi è nel backlog.

## 7. Volti

Il riconoscimento raggruppa i volti ignoti come "Persona N" e l'utente assegna i nomi una volta sola; le impronte biometriche sono cifrate.

### Modalità

| Modalità | Cosa fa |
| --- | --- |
| `Off` | Nessuna analisi dei volti |
| `DetectOnly` | Volti e occhi chiusi, senza impronte |
| `Recognize` | Impronte, riconoscimento dei noti, clustering degli ignoti |

Impostabile globalmente e sovrascrivibile per profilo.

### Configurazione

```json
"faces": {
  "mode": "Recognize",
  "modelPack": "buffalo_l",
  "minFaceSizePx": 48,
  "detectionConfidence": 0.6,
  "matchThreshold": 0.45,
  "clusterMinSize": 3,
  "writeRegionsToXmp": true,
  "writeNamesAsKeywords": true
}
```

| Parametro | Nota |
| --- | --- |
| `modelPack` | buffalo\_l (default), buffalo\_s; buffalo\_sc escluso (senza landmark, inutile per gli occhi); YuNet/SFace come alternativa con licenza permissiva, **senza rilevamento degli occhi chiusi** (occhi sempre "sconosciuto", decisione del 12 ott 2026) |
| `matchThreshold` | Valore iniziale da tarare sui dati reali |
| `clusterMinSize` | Sotto soglia i volti restano "non assegnati" |

Valori della fase 2 (13 ott 2026): impronte ArcFace w600k\_r50 (buffalo\_l, 512 valori) o SFace (128 valori) su ritaglio allineato ai 5 punti (Umeyama sul modello ArcFace, poi trasformazione affine come OpenCV; parità: coseno ≥ 0,99999 con insightface e OpenCV); nessuna impronta per volti sotto 48 px alla scala di riferimento. Riconoscimento: coseno con i campioni ≥ 0,45 (ArcFace) o 0,363 (SFace, valore OpenCV); se il secondo candidato dista meno di 0,05 il volto resta non assegnato; nella fascia di 0,10 sotto soglia diventa un suggerimento (chip ambra); campioni: fino a 5 per persona per anno, dai volti confermati. Clustering: HDBSCAN in C# (F2-2), selezione "excess of mass", `min_cluster_size` 3, `min_samples` 2 (F2-6), Prim esatto fino a 3.000 volti per blocco, oltre su grafo k-NN (k = 15); parità con scikit-learn 1.9 (altezze del dendrogramma identiche, partizioni identiche).

### Flusso

1. Rilevamento (condiviso con il culling) e impronte.
2. Confronto con `PersonPrototypes` delle persone note.
3. Clustering HDBSCAN solo dei non assegnati, a blocchi (per anno o cartella), con ricerca per vicini approssimata: con \~100.000 volti la matrice completa delle distanze non sta in memoria.
4. Revisione: rinomina, unisci, separa, ignora; gruppi ordinati per dimensione.
5. Scrittura: regioni MWG e `Persone|Nome` come keyword.

Scrittura (fase 2): solo le persone con nome e non ignorate, solo per foto confermate; `Nome` nelle keyword piatte e `Persone|Nome` in quelle gerarchiche; regioni MWG (`mwg-rs:RegionInfo`, area normalizzata al centro, tipo `Face`) scritte da exiftool come struttura completa: le regioni di altri programmi (nome non gestito da PhotoFlow) restano; un RAW riceve le persone del JPEG gemello (sidecar).

**Volti nella foto e volti aggiunti a mano** (13 ott 2026, su richiesta dell'utente):

- **Vedere chi c'è.** L'anteprima grande della Consultazione e la foto aperta dalla pagina di una persona mostrano un riquadro per ogni volto, con il nome sopra ("?" per un volto non riconosciuto), e l'elenco "Persone nella foto". In Persone il volto della pagina è evidenziato. Il tasto V mostra o nasconde i riquadri.
- **Dire chi è un volto.** Nell'anteprima grande un clic su un riquadro (un "?" o un nome sbagliato) apre la stessa scelta della persona. L'assegnazione diventa manuale (I5). Le due persone, la nuova e l'eventuale precedente, rifanno i campioni: l'impronta del volto aiuta a riconoscere la persona nelle altre foto.
- **Aggiungere un volto a mano.** Serve quando il rilevamento non ha trovato un volto. Nell'anteprima grande della Consultazione si preme N e si trascina un riquadro sul volto. Poi si sceglie la persona da un elenco: quelle con nome, in ordine alfabetico, oppure "+ Nuova persona…".
- **Come viene salvato.** Il volto è dell'utente: assegnazione manuale, versione `manual`, nessuna impronta. Per I5 nessuna analisi lo cancella o lo riassegna. Se una rigenerazione con un altro modello trova lo stesso volto (IoU ≥ 0,5, come F5-3), il volto rilevato prende il posto del riquadro disegnato e tiene la persona e l'assegnazione manuale. Va nell'XMP come regione MWG con il nome, come gli altri volti con nome. Senza impronta non contribuisce ai campioni della persona, quindi non aiuta a riconoscerla altrove.

**Modello di riconoscimento e azzeramento delle persone** (15 ott 2026, su richiesta dell'utente, P22):

- **Scelta del riconoscimento.** Il modello delle impronte si sceglie a parte dal rilevatore (Impostazioni › Modelli › "Riconoscimento delle persone"): ArcFace w600k\_r50 (in buffalo\_l), SFace, oppure **AdaFace IR-101** (WebFace12M, opzionale). Senza una scelta vale l'abbinamento di prima (buffalo\_l → ArcFace, YuNet → SFace). Il rilevatore e gli occhi restano quelli scelti (buffalo\_l o YuNet): tutti forniscono i 5 punti per l'allineamento.
- **AdaFace IR-101.** Migliore sui volti piccoli e sfocati: su LFW (6.000 coppie, laboratorio del 15 ott 2026) errori da 1,65% a 0,80% con volti di 16 px e da 1,23% a 0,72% con sfocatura forte, pari sui volti normali. Stesso ritaglio e stessa normalizzazione di ArcFace, 512 valori. Soglie tarate su LFW a parità di falsi riconoscimenti con ArcFace (3 milioni di coppie diverse): assegnazione **0,42**, suggerimento 0,32 (fascia di 0,10). I pesi (Hugging Face `minchul/cvlface_adaface_ir101_webface12m`, revisione fissa, SHA-256) si scaricano dall'app e si convertono in ONNX sul PC con l'ambiente Python della conversione del livello D (`tools/vlm`), su CPU; parità con la rete PyTorch degli autori (coseno minimo 0,99999 su 200 volti, CPU e CUDA; `tools/lab/adaface_refs.py`). La scheda non ha una licenza propria e rimanda a quella del set di addestramento (WebFace): come buffalo\_l, **solo uso non commerciale, con accettazione esplicita**.
- **Cambio di modello.** Impronte incompatibili tra modelli: dopo la scelta la rigenerazione dei volti rifà rilevamento e impronte (F5-3: assegnazioni trasferite per sovrapposizione, I5), con ambito anche **"Tutto il catalogo"**.
- **Ripartire da zero con le persone.** Nella rigenerazione dei volti un'opzione esplicita, spenta di default, cancella in **tutto il catalogo** le persone (anche con nome), tutte le assegnazioni (anche manuali), i suggerimenti, i campioni e i volti disegnati a mano; si applica con la rigenerazione completa (mai con la prova su campione), dopo una conferma con i conteggi, ricontati nell'anteprima prima di "Applica"; prima dell'applicazione PhotoFlow fa un backup del catalogo e dopo raggruppa di nuovo i volti di tutti gli anni. Non è un processo automatico che sovrascrive le scelte dell'utente (I5) ma una sua azione esplicita, come "Elimina persona". Gli XMP già scritti vengono riscritti: spariscono regioni e keyword delle persone cancellate; le regioni scritte da altri programmi restano.

### Regole di qualità

| Regola | Motivo |
| --- | --- |
| Soglie che preferiscono dividere i gruppi | Unire è un clic, separare è un lavoro |
| Campioni aggiornati per persona | Il volto dei bambini cambia negli anni |
| Eliminare una persona cancella tutte le sue impronte | Nessun residuo |

### Protezione delle impronte

| Aspetto | Specifica |
| --- | --- |
| Cosa è cifrato | Solo i vettori delle impronte (cifratura applicativa con le librerie standard .NET) |
| Chiave | Default: account utente del sistema (Windows; portachiavi su Mac/Linux); alternativa: password |
| Chiave non disponibile | Richiesta della password, oppure ricalcolo delle impronte; le assegnazioni restano (in chiaro, legate alla posizione del volto) |
| Esportazione | Impronte cifrate con password di esportazione, oppure escluse e ricalcolate sulla destinazione |

Fase 2 (13 ott 2026, F2-3): AES-256-GCM per impronta (versione, identificativo della chiave, nonce, tag, testo cifrato; dati associati fissi); chiave dati in `face-key.json` nella cartella di configurazione, protetta da DPAPI dell'account Windows e, se l'utente la imposta, da una password (PBKDF2-SHA256, 600.000 iterazioni) che la apre su altri account o PC e la riprotegge per l'account corrente. Senza chiave leggibile (P13): galleria consultabile, riconoscimento sospeso, "Inserisci password" o "Ricalcola impronte" (nuova chiave, impronte ricalcolate dalle anteprime, nomi e assegnazioni intatti). Portachiavi Mac/Linux nel backlog: lì serve la password.

### Licenza

buffalo\_l è solo per ricerca non commerciale: mai incluso nel repository, scaricato al primo uso con accettazione esplicita della licenza.

## 8. Tagging e OCR motorsport

Ogni tag ha un identificativo interno stabile in inglese ed etichette per lingua; ogni assegnazione registra fonte e confidenza.

### Fonti dei tag

| Fonte | Produce | Lingua di origine |
| --- | --- | --- |
| RAM++ | Tag generici (4.585 possibili) | Inglese |
| CLIP zero-shot | Tag personalizzati (descrizione inglese per il modello + etichetta italiana) | Definita dall'utente |
| Job | Nome dell'evento | Utente |
| Volti | `Persone\|Nome` | Utente (nomi mai tradotti) |
| OCR | `Motorsport\|Numero N`, `Motorsport\|Pilota\|Nome`, `Motorsport\|Team\|Nome` (pilota e team solo da elenco iscritti; con equipaggio solo numero e team, D18) | — (non tradotti) |
| Manuale | Tag dell'utente | Utente |

I dati EXIF non diventano keyword: sono già ricercabili nel catalogo.

### Vocabolario e traduzione

| Livello | Contenuto | Trattamento |
| --- | --- | --- |
| Nucleo | \~500 tag più frequenti nell'archivio dell'utente | Tradotti con un LLM e rivisti dall'utente |
| Resto | Tag rari | Traduzione automatica, "non rivisti": scritti solo in inglese finché non rivisti |
| Lista nera | Tag inutili (es. "image", "photo") | Mai scritti |

Categorie gerarchiche: `Persone`, `Luoghi`, `Natura`, `Veicoli`, `Eventi`, `Animali`, tramite tabella di mappatura sul nucleo.

Fase 3 (13 ott 2026, F3-2, F3-3): le etichette italiane e una categoria predefinita di tutti i 4.585 tag RAM++ sono preparate durante lo sviluppo con un LLM e incluse nell'applicazione (`vocabulary/ramplus.tsv`), tutte "non riviste" (la tavola 5c parla di LLM locale: un modello locale richiederebbe gigabyte e GPU per poche centinaia di parole, con risultati non riproducibili). Una piccola lista nera predefinita (image, photo, picture, snapshot, file photo, screenshot, stock) si applica solo al primo inserimento del tag; le modifiche dell'utente non vengono mai sovrascritte. Il nucleo sono i 500 tag RAM++ più frequenti, mostrato dopo 1.000 foto con tag (P15); revisione a righe (Invio approva, F2 modifica, B lista nera, K categoria, Ctrl Z annulla).

### Soglie

| Fonte | Soglia |
| --- | --- |
| RAM++ | Soglie ufficiali per tag + regolatore globale |
| CLIP | Soglia per tag + margine minimo sul secondo candidato |

Fase 3: RAM++ usa le soglie ufficiali per classe più un regolatore globale (Vocabolario, applicato dalle analisi successive). Un tag personalizzato CLIP (tavola 5d) si assegna quando il coseno tra immagine e descrizione inglese raggiunge la sua soglia e supera di almeno il suo margine il miglior altro tag personalizzato; "Prova sull'archivio" conta le foto senza salvare, "Salva e rianalizza" riassegna i tag personalizzati su tutte le foto con embedding.

### Provenienza

| Regola | Motivo |
| --- | --- |
| Fonte e confidenza registrate | Filtri e rimozioni mirate |
| Tag manuali mai rimossi da una rianalisi | Lavoro dell'utente protetto |
| Tag automatici rimossi dall'utente → soppressi | Non vengono reinseriti |

Fase 3: una riga per foto e tag (`AssetTags`) con fonte, confidenza, versione del modello e stato; ogni fonte (RAM++, CLIP, evento) sostituisce solo i propri tag automatici; un tag manuale non viene mai tolto da un'analisi; qualsiasi tag tolto dall'utente (anche manuale) resta "soppresso" finché l'utente non lo aggiunge di nuovo (test `TagInvariantTests`, scritti prima del codice).

### Didascalie

Florence-2 produce solo inglese; il VLM del livello D (Qwen3.5-9B, prima Qwen2.5-VL) può produrre italiano ma solo con GPU. Scrittura in `dc:description` opzionale.

Fase 5 (13 ott 2026, F5-1, F5-2):

- **Come si genera.** Livello C con Florence-2 base-ft, compito "didascalia dettagliata". La ricerca a fascio è identica a transformers: 3 fasci, nessun trigramma ripetuto, chiusura anticipata. Costo: 0,9 s per foto su GPU (2,5 s su CPU); nell'analisi con 4 lavori in parallelo +644 ms per foto. È facoltativo e si sceglie nell'import.
- **Conservazione (I5).** Le didascalie si salvano in `Captions` con la versione del modello. Quella scritta o cancellata dall'utente (D in Consultazione, o la descrizione cambiata in un altro programma) non viene mai sostituita.
- **Ricerca e XMP.** Il testo delle didascalie è cercabile. `dc:description` si scrive solo con la preferenza attiva (spenta per default).
- **Livello D.** Rinviato (F5-2), poi riaperto dall'utente il 13 ott 2026: fase 5b (`docs/plans/fase-5b.md`). Qwen2.5-VL-7B scrive una didascalia in italiano (una o due frasi, oggettive), solo con GPU NVIDIA ≥ 12 GB (P16, rivista il 13 ott 2026). Dal 15 ott 2026 il modello predefinito è Qwen3.5-9B (U1 rivista): stesso prompt, risposta senza «ragionamento» (blocco think vuoto), penalità di ripetizione 1,05 contro le frasi ripetute della decodifica greedy. Il livello C (inglese) resta. In Consultazione e in `dc:description` l'italiano è x-default, l'inglese la lingua alternativa.
- **Come gira il livello D** (fase 5b). Pesi originali scaricati dall'utente e convertiti dall'app (Impostazioni › Modelli) con il Python del PC in un ambiente privato. Il modello gira in un processo separato (ONNX Runtime GenAI, eccezione U4). Non gira per foto insieme agli altri modelli (insieme superano i 21 GB): è una **passata a fine job**, che aspetta le analisi in corso e libera la GPU dagli altri modelli (~10,6 GB per il solo VLM, ~2 s per foto). Nella rigenerazione funziona allo stesso modo, e le didascalie passano dall'anteprima come le altre. Dopo 3 errori di fila la passata si ferma, senza far fallire il job: le foto rimaste aspettano la passata successiva.

### OCR motorsport

1. D-FINE rileva auto/moto.
2. OCR solo sul ritaglio di ciascun veicolo.
3. Accettati solo i numeri presenti nell'elenco iscritti.
4. Nelle raffiche il numero si propaga a maggioranza.

| Caso | Gestione |
| --- | --- |
| Numeri estranei (sponsor, targhe) | Ignorati se non in elenco |
| Numero parziale, confusione 6/9 o 1/7 | Candidati multipli → "da rivedere" |
| Più auto nella foto | Tag per ciascuna; soggetto principale evidenziato |
| Vettura con un pilota | XMP: numero + pilota (+ team) |
| Vettura con equipaggio | XMP: solo numero (+ team); equipaggio associato nel catalogo, ricercabile e segnalato come "equipaggio" |
| Senza elenco iscritti | Solo il numero, e solo se letto da solo (non dentro un'altra scritta) con confidenza ≥ 0,85 |

Elenco iscritti: import CSV/Excel (numero, pilota/i, team, categoria, vettura) o inserimento manuale; riutilizzabile su più eventi. Correzione rapida dalla vista "Numeri" della revisione.

Fase 4 (13 ott 2026, F4-1..F4-5):

- **Letture.** Per le foto il cui profilo chiede l'OCR (Motorsport), OCR sul ritaglio di ciascuna vettura rilevata (le 6 più grandi, margine del 2%, ritaglio ingrandito al lato corto di 736 px dal rilevatore di testo); senza vetture rilevate, sull'intero fotogramma. Le letture si salvano per vettura in `OcrResults` con la versione del modello.
- **Scelta del numero (F4-3, fase di lotto dopo i raggruppamenti).** Contano i gruppi di 1-3 cifre. Con un elenco valgono solo i suoi numeri: certo se la confidenza è ≥ 0,7 e distacca di 0,1 il secondo candidato in elenco. Le cifre spesso confuse (6/9, 1/7) danno un candidato con penalità 0,9, ridotta a 0,5 quando anche il numero letto è in elenco: un 9 nitido resta 9 anche se il 6 è iscritto. Le letture parziali (prefisso o suffisso di numeri dell'elenco) danno candidati da rivedere. Senza elenco vedi la tabella.
- **Raffiche.** La vettura principale delle foto in dubbio prende il numero certo della maggioranza della raffica, se compatibile con i suoi candidati (origine "dalla raffica").
- **Correzioni e conservazione (I5).** I numeri dati dall'utente sono manuali e non vengono mai sostituiti da una nuova lettura. La fase si ripete quando cambia l'elenco del job (scelto nell'import o nella vista Numeri).
- **Tag (D18).** `Motorsport|Numero N`, `Motorsport|Team|Nome` e, per il pilota singolo, `Motorsport|Pilota|Nome`. Piloti (tutto l'equipaggio), team e vettura dell'elenco sono cercabili nella Consultazione.
- **Vista Numeri (2c, tasto N dalla Revisione).** Elenchi: da risolvere (per gruppo di candidati, es. "6 o 9"), in elenco, altro (nessun numero, fuori elenco). Tasti: 0-9 digita, ⌫ cancella una cifra, Invio conferma il numero digitato o il primo candidato e va avanti, Canc nessun numero, ↑ ↓ candidati e numeri dell'elenco, ← → foto, Pag ↑ Pag ↓ elenco, P propagazione alla raffica, Ctrl Z annulla. Pannello dell'elenco: scelta, import CSV/Excel, modifica come testo (una vettura per riga).
- **Misure (set locale, salone moto 2015, 35 foto, 4 analisi in parallelo, RTX 3090).** L'OCR costa 97 ms per foto (da 1,55 a 1,34 foto/s). Senza elenco: 2 numeri certi su 34 vetture, entrambi scritte di modelli, da correggere nella vista Numeri; con l'elenco sarebbero ignorati. Il set non contiene numeri di gara: la qualità sui numeri resta da verificare su foto in pista (§17).

## 9. Catalogo e modello dati

Catalogo SQLite, foto identificate dall'hash del contenuto: tag, volti e decisioni seguono il file anche se spostato o rinominato.

### Posizione del catalogo

| Elemento | Posizione |
| --- | --- |
| Catalogo | Configurabile: locale (proposto) o percorso di rete. Si sceglie al primo avvio, prima che il catalogo venga creato, e si cambia in Impostazioni › Catalogo (vedi sotto) |
| Miniature e cache | Sempre locali (\~2 GB per 60.000 foto), ricostruibili |
| Profili e impostazioni | File JSON nella cartella dati utente |

| Catalogo in rete: protezione | Comportamento |
| --- | --- |
| Modalità journal | Rollback automatico su percorso di rete (WAL non funziona su filesystem di rete) |
| Un PC alla volta | File di lock; apertura rifiutata con messaggio chiaro (dopo 2 s di nuovi tentativi: un processo appena terminato può rilasciare il lock in ritardo) |
| Backup | A ogni chiusura e ogni giorno (all'avvio, se l'ultimo è più vecchio di 24 h); si tengono gli ultimi 10 (decisione B1); cache delle miniature entro 4 GB, temporanei oltre 24 h e archivi del pacchetto GPU installato ripuliti |
| Integrità | Controllo all'apertura; ripristino proposto dall'ultimo backup. Un ripristino scelto nelle Impostazioni si applica al prossimo avvio; il catalogo sostituito si sposta in `backup/replaced-*` e non si cancella |

**Scelta e cambio di posizione** (13 ott 2026, su richiesta dell'utente; la tavola 1a mostrava solo il percorso, "modificabile in Impostazioni"). La posizione scelta è salvata in `config/catalog-location.json`, nella cartella dati locale.

- **Primo avvio.** Se non esiste ancora un catalogo, prima di crearlo PhotoFlow chiede la cartella e propone quella locale. Se nella cartella scelta c'è già un catalogo PhotoFlow valido, si usa quello.
- **Sposta…** Il catalogo viene copiato nella nuova cartella al prossimo avvio, a catalogo chiuso: copia con il backup di SQLite e controllo d'integrità. L'originale resta dov'era e non viene mai cancellato. Se la cartella contiene già un catalogo, lo spostamento è rifiutato.
- **Apri un catalogo esistente…** Si usa dal prossimo avvio, dopo averne verificato l'integrità; quello attuale resta intatto.
- **Errori.** Se all'avvio la copia o l'apertura non riesce (lock, file danneggiato, cartella non raggiungibile), si resta sul catalogo di prima e il motivo compare nelle Impostazioni.
- **Cosa resta locale.** Backup, miniature e cache restano nella cartella dati locale, qualunque sia la posizione del catalogo.

Esportazione/importazione: file unico (es. `.pfcatalog`) con database, galleria persone e configurazione delle librerie; rimappatura dei percorsi all'importazione.

Fase 5 (13 ott 2026, F5-4). Il `.pfcatalog` è uno zip con `manifest.json`, una copia coerente del catalogo (API di backup di SQLite), i profili personalizzati e, a scelta, le miniature.

- **Impronte.** Con una password d'esportazione viaggiano, ricifrate con una chiave casuale protetta dalla password (PBKDF2-SHA256 e AES-GCM, come la chiave locale). Senza password restano indietro: nomi e assegnazioni viaggiano, le impronte si ricalcolano dalla pagina Persone.
- **Importazione.** Prepara il catalogo con le radici delle librerie rimappate (✓ cartella trovata, ? no) e le impronte cifrate con la chiave locale. Sostituisce quello attuale al prossimo avvio, come un ripristino; il catalogo sostituito resta in `backup/replaced-*`. Profili e miniature si aggiungono senza sostituire file esistenti.
- **Misure.** Catalogo locale (230 foto): esportazione 1,1 s (1,9 MB), importazione preparata in 1,2 s.

### Tabelle

| Area | Tabella | Campi principali |
| --- | --- | --- |
| Librerie | `Libraries` | Nome, percorso radice, politica XMP, sola lettura, ultima scansione |
| Foto | `Assets` | Hash rapido (dimensione + primi/ultimi 64 KB), hash completo (facoltativo, in background), formato, gestore, dimensioni, data di scatto + fonte, EXIF principali, stato |
|  | `AssetLocations` | Asset, libreria, percorso relativo, ultima volta visto |
|  | `AssetPairs` | Gemelli RAW+JPEG |
| Lavori | `Jobs` | Tipo, sorgente, evento, profilo/automatico + riserva, stato, priorità, istantanea delle impostazioni |
|  | `JobItems` | Avanzamento per foto e fase, errori (punto di ripresa) |
| Modelli | `Models` | Nome, versione, SHA-256, licenza, funzione, stato (in prova/attivo/ritirato) |
| Analisi | `AnalyzerResults` | Asset, analizzatore, versione modello, profilo, punteggi |
|  | `Embeddings` | Asset, modello, vettore (BLOB) |
| Gruppi | `Groups` | Tipo, profilo assegnato e confidenza, origine |
|  | `GroupMembers` | Gruppo, asset, posizione, punteggio |
| Decisioni | `Decisions` | Esito automatico e motivazioni, esito utente, rating, etichetta |
|  | `Corrections` | Correzioni dell'utente con i segnali dell'epoca |
| Tag | `Tags` | Identificativo, categoria, fonte, rivisto, lista nera |
|  | `TagLabels` | Tag, lingua, etichetta |
|  | `AssetTags` | Asset, tag, fonte, confidenza, versione modello, stato (attivo/soppresso) |
|  | `CustomTags` | Descrizione CLIP, soglia, margine |
| Volti | `Faces` | Asset, riquadro, landmark, confidenza, occhi, impronta cifrata (facoltativa), persona, origine assegnazione, somiglianza |
|  | `Persons` | Nome, ignorata, volto di copertina |
|  | `PersonPrototypes` | Impronte rappresentative (cifrate) |
| Testi | `Captions` | Asset, lingua, testo, versione modello, origine (automatica/manuale) |
|  | `OcrResults` | Asset, vettura e suo riquadro, riquadro del testo, testo, confidenza, versione modello |
| Motorsport | `EntryLists` | Nome, file d'origine (riutilizzabile su più eventi) |
|  | `Entries` | Elenco, numero, pilota/i, team, categoria, vettura |
|  | `JobEntryLists` | Job, elenco usato |
|  | `VehicleNumbers` | Asset, vettura, riquadro, numero, stato (certo/da rivedere/nessuno/fuori elenco), origine (automatica/raffica/manuale), candidati |
| Sincronizzazione | `XmpSync` | Ultima scrittura, firma delle modifiche, scrittura in sospeso, keyword scritte, stato e contenuto del file XMP |
|  | `XmpSyncLog` | Asset, campo, valore, chi vince (altro programma/catalogo), nota |
| Rigenerazione | `ReanalysisStaging` | Job, asset, parte, risultati in attesa di conferma (JSON) |
| Sistema | `UndoLog`, `SchemaVersion` | Operazioni annullabili; versione schema |

I risultati automatici conservano la versione del modello che li ha prodotti (rianalisi, anteprima differenze, ritorno indietro).

### Scelte tecniche

| Tema | Scelta |
| --- | --- |
| Ricerca testuale | SQLite FTS5 su tag (IT+EN), persone, eventi, didascalie, percorsi |
| Ricerca per similarità | Vettori in memoria (\~60-120 MB per 60.000 foto), confronto diretto |
| Migrazioni | Script SQL numerati, solo in avanti, backup automatico prima di ogni migrazione |
| Futuro PostgreSQL | FTS e vettori dietro `ISearchIndex`; resto SQL standard |

## 10. Requisiti non funzionali

Le invarianti di sicurezza dei dati non sono configurabili e vanno coperte da test automatici.

### Invarianti

| # | Invariante |
| --- | --- |
| I1 | Nessuna scrittura né cancellazione sulla sorgente di import (sorgente dei job `Import`; i file di una libreria seguono la sua politica XMP, vedi D21) |
| I2 | RAW mai modificati (solo sidecar) |
| I3 | Nessuna fase successiva su un file non verificato |
| I4 | Nessun file dell'utente eliminato automaticamente; gli scarti si spostano solo su richiesta |
| I5 | Tag e decisioni manuali mai sovrascritti da processi automatici |

Ambito di I4 (decisione B1, 12 ott 2026): "file dell'utente" sono foto, RAW, sidecar, copie `_original`, file delle sorgenti e delle destinazioni di import, cartelle delle librerie. I file interni di PhotoFlow, solo dentro le sue cartelle di `IAppPaths` (backup del catalogo, cache delle miniature, temporanei, download dei modelli e del pacchetto GPU), si possono ruotare o pulire con regole scritte qui: backup del catalogo oltre gli ultimi N; cache delle miniature oltre un limite di spazio (si ricostruisce); temporanei di sessioni concluse; archivi scaricati dopo l'installazione verificata. Un test vieta qualunque cancellazione fuori da quelle cartelle.

### Affidabilità

| Requisito | Specifica |
| --- | --- |
| Errori su singoli file | Non fermano il job; motivo registrato; nuovo tentativo possibile |
| File corrotti | Elencati in un report, mai spostati o modificati |
| Crash | Report locale; al riavvio proposta di riprendere i job interrotti |
| Ripresa | Avanzamento salvato per foto: si perde al massimo l'ultimo blocco |
| Annulla | Ogni modifica di rating, tag o persona è annullabile |

### Archivi grandi (fase 1b)

| Requisito | Specifica |
| --- | --- |
| Priorità | Modalità a bassa priorità con limiti di CPU/GPU/memoria |
| Ordine | Per cartella o dalla più recente |
| Stima del tempo | Dal benchmark iniziale, corretta durante l'esecuzione |
| Sorgenti lente | Coda limitata: rete e GPU non si bloccano a vicenda |
| Identità | Hash rapido al primo passaggio, hash completo in background |

### Log e privacy

| Requisito | Specifica |
| --- | --- |
| Log | File locali a rotazione, livello configurabile, log per job |
| Telemetria | Nessuna |
| Rete | Solo download modelli e controllo aggiornamenti, entrambi disattivabili; funzionamento completamente offline dopo il setup |

### Prestazioni (obiettivi)

| Area | Obiettivo |
| --- | --- |
| Interfaccia | Nessuna operazione bloccante; risposta ai comandi < 100 ms |
| Griglia | Scorrimento fluido con 60.000 miniature |
| Ricerca | < 1 s su 60.000 foto |
| Duplicati | Confronto embedding su 60.000 foto: secondi su GPU, minuti su CPU |

Misure della fase 3 (13 ott 2026, F3-4): su un catalogo sintetico di 60.000 foto (8 tag, 0-3 volti, embedding 512-d ciascuna) ogni ricerca richiede meno di 0,26 s (filtri esatti 50-115 ms, parole 0,2 s, significato e simili 0,25 s); l'indice testuale si costruisce una volta (~1,5 s a 60.000 foto) e poi si aggiorna foto per foto dalle modifiche segnate da trigger (ricerca dopo una persona rinominata e 400 tag cambiati: 0,26 s). Ricerca testuale FTS5 su nomi file e cartelle, etichette dei tag nelle due lingue e nomi delle persone; significato e "trova simili" con scansione esatta degli embedding in memoria. Duplicati (F3-5, F3-6): copie esatte = stesso contenuto due volte nella stessa libreria (le copie tra librerie sono backup e non vengono proposte); quasi-duplicati = coseno CLIP ≥ 0,95 su tutte le coppie (scansione esatta in parallelo) confermato da un hash percettivo della miniatura (≤ 10 bit su 64); gemelli RAW+JPEG e file già spostati in `_duplicati`/`_scarti` esclusi.

## 11. Installazione e aggiornamenti

Pacchetto self-contained con Velopack, modelli scaricati al primo uso; durante lo sviluppo si esegue dalla cartella di build e l'installer si genera solo alle versioni di riferimento.

### Pacchetto

| Aspetto | Specifica |
| --- | --- |
| Runtime .NET | Incluso (self-contained) |
| Installer e aggiornamenti | Velopack 1.2.161, id del pacchetto `FoyerLabs.PhotoFlow`: il programma sta in `%LOCALAPPDATA%\FoyerLabs.PhotoFlow`, separato dai dati in `%LOCALAPPDATA%\PhotoFlow` (Velopack alla disinstallazione cancella la propria cartella). Feed: le release `vX.Y.Z` del repository GitHub; le pre-release dei modelli non vengono mai proposte |
| Release su GitHub | Versioni dell'app con tag `vX.Y.Z` (alle versioni di riferimento). I file dei modelli esportati da noi stanno in release `models-<modello>-<n>` dello stesso repository, marcate come pre-release e mai «Latest», con il titolo "Model file · …" (decisione dell'utente del 13 ott 2026) |
| Varianti | Windows: CUDA (NVIDIA) + CPU, pacchetto GPU scaricato al primo uso; Mac: CoreML; Linux: CPU/CUDA |
| Firma del codice | Nessuna per ora (avviso SmartScreen al primo avvio) |
| exiftool | Windows: eseguibile incluso; Mac/Linux: script Perl incluso; percorso personalizzabile. Scaricato in fase di build con versione fissata e SHA-256 (nessun binario nel repository, F1-5) |

### Modelli

| Aspetto | Specifica |
| --- | --- |
| Distribuzione | Download al primo uso dal manifest, verifica SHA-256 |
| Offline | Importazione di un pacchetto modelli da cartella |
| Posizione | Configurabile |
| Licenze | Mostrate prima del download; non commerciali contrassegnate |
| Aggiornamenti | Separati dall'applicativo: flusso prova → rianalisi → conferma |

| Configurazione | Modelli | Spazio indicativo |
| --- | --- | --- |
| Fase 1 | D-FINE, buffalo\_l | \~0,4 GB |
| + raffiche e tagging A+B | + CLIP, RAM++ int8 | \~1,5-2 GB |
| + didascalie e OCR | + Florence-2, PaddleOCR | \~3-3,5 GB |
| + VLM | + Qwen3.5-9B convertito (U1 rivista il 15 ott 2026) | \~6 GB, più 18 GB di pesi originali da tenere per riconvertire |

### Dati sul disco

| Contenuto | Posizione predefinita | Alla disinstallazione |
| --- | --- | --- |
| Applicativo | Cartella di installazione | Rimosso |
| Configurazione, profili, vocabolario | Cartella dati utente | Conservati salvo scelta |
| Catalogo | Configurabile | Conservato salvo scelta |
| Modelli | Configurabile | Conservati salvo scelta |
| Miniature e cache | Cartella dati utente | Rimosse |

### Aggiornamenti dell'applicativo

Controllo all'avvio in background; installazione solo dopo conferma e mai durante un job; migrazione del catalogo con backup preventivo; versione precedente disponibile per il ripristino. Fatto per la v0.1.0 (13 ott 2026): Impostazioni › Generali › Aggiornamenti (versione, "Cerca una nuova versione all'avvio" attivo per default, "Controlla ora", "Scarica, installa e riavvia" bloccato durante un job); avviso nella barra di stato. Alla disinstallazione si svuotano cache, miniature e temporanei; configurazione, catalogo, modelli, backup e log restano.

### Requisiti indicativi

| Risorsa | Minimo | Consigliato |
| --- | --- | --- |
| Sistema | Windows 10/11 x64 | Windows 11 |
| RAM | 8 GB | 16 GB o più |
| GPU | Nessuna | GPU DirectX 12; NVIDIA per la build CUDA |
| Disco | \~1 GB + modelli + miniature | SSD per catalogo e miniature |

## 12. Brief per Claude Design

Obiettivo: sistema visivo e 11 schermate di un'applicazione desktop per fotografi, da implementare poi in Avalonia (non web). Prima il sistema visivo, poi le schermate.

### Vincoli

| Vincolo | Dettaglio |
| --- | --- |
| Piattaforma | Desktop (Windows prioritario), finestra ridimensionabile, larghezza minima \~1280 px |
| Implementazione | Avalonia: evitare effetti esclusivamente web; segnalare soluzioni che richiedono componenti personalizzati |
| Tema | Scuro con grigi neutri di default (non falsano i colori delle foto); tema chiaro disponibile |
| Lingue | Italiano e inglese: prevedere testi più lunghi del 30% senza rompere il layout |
| Contenuto protagonista | Le foto: interfaccia sobria, cromia minima attorno alle immagini |
| Tastiera | Ogni azione frequente ha una scorciatoia visibile |

### Sistema visivo da definire

Palette (con colori semantici per gli esiti: scarto, da rivedere, migliore, simile, tenuta), tipografia, spaziature, griglia, componenti (miniatura con stato, chip tag, chip persona, barra di avanzamento per fase, filtri a faccette, pannello dettagli, dialoghi di conferma).

### Schermate

| # | Schermata | Contenuto | Note di design |
| --- | --- | --- | --- |
| 1 | Home | Librerie, job attivi, avviso "scheda rilevata", azioni rapide | Punto di partenza, poco testo |
| 2 | Import (procedura guidata) | Sorgente → evento, profilo (o automatico + riserva), destinazioni, volti, politica XMP → avvio | 3 passi, impostazioni ricordate |
| 3 | Job | Avanzamento per fase, stima tempo, pausa/ripresa, priorità, log | Più job in parallelo |
| 4 | Revisione | Griglia per raffiche, filtri per esito, confronto 2-4 affiancati con zoom sincronizzato, cambio profilo per gruppo, vista "Numeri", conferma e scrittura XMP | Schermata principale: velocità da tastiera |
| 5 | Persone | Gruppi ordinati per dimensione; rinomina, unisci, separa, ignora; pagina persona | Volti ritagliati, selezione multipla |
| 6 | Consultazione | Ricerca libera + filtri (tag, persona, data, evento, EXIF, rating, esito), pannello delle faccette con conteggi (tag in gruppi AND/OR, eventi, persone), griglia, pannello dettagli con tag modificabili e volti evidenziati, "Apri in Photoshop", "Apri con app predefinita", "Mostra in Esplora risorse" | Ricerca in linguaggio naturale nello stesso campo |
| 7 | Duplicati | Gruppi esatti e quasi-duplicati, scelta di quale tenere | Confronto affiancato |
| 8 | Librerie | Radici, politica XMP, sola lettura, nuova scansione, rimappatura percorsi |  |
| 9 | Vocabolario | Revisione traduzioni del nucleo, lista nera, tag CLIP personalizzati, mappatura categorie | Revisione rapida a righe |
| 10 | Profili | Elenco, duplica, modifica pesi e soglie | Predefiniti non modificabili |
| 11 | Impostazioni | Hardware e benchmark, modelli (per funzione, con licenza e spazio), catalogo (posizione, backup, esporta/importa, cifratura impronte), lingua, template percorsi, exiftool, modalità prudente/normale | Sezioni a schede |

Modalità aggiuntiva della schermata 4: **etichettatura** (sezione 14), con un tasto per foto, verdetto nascosto ("alla cieca"), sessioni da \~100 foto e pannello metriche.

**Consultazione: pannello delle faccette** (13 ott 2026, su richiesta dell'utente; usa il componente "Filtri a faccette" del sistema visivo, che la tavola 4d non mostrava).

Un pannello a sinistra della griglia, richiudibile con il tasto F, ha tre sezioni. Ogni sezione mostra i valori con il numero di foto e si aggiorna a ogni selezione. Il pannello si somma alla ricerca libera e agli altri filtri: data, fotocamera, stelle, esito e gettoni del campo di ricerca.

- **Tag.** Etichetta italiana se disponibile, altrimenti l'inglese; eventi e tag in lista nera esclusi; casella per filtrare l'elenco.
  - *Gruppi combinabili.* La selezione è fatta di gruppi: dentro un gruppo i tag sono in OR, tra i gruppi in AND. Esempio: (auto OR moto) AND pista.
  - *Clic.* Un interruttore AND/OR dice cosa fa il clic: in AND il tag apre un nuovo gruppo, in OR si aggiunge all'ultimo gruppo. Ctrl+clic fa l'altra azione.
  - *Conteggi.* Mostrano sempre l'effetto del clic. In AND: quante foto del risultato hanno il tag, cioè quante ne restano. In OR: quante foto si aggiungono al risultato ("+N").
  - *Quali tag compaiono.* In AND solo i tag presenti nel risultato. In OR anche quelli che aggiungono foto; un tag del risultato che non aggiunge nulla resta in elenco con "+0", così con Ctrl+clic si può ancora restringere.
  - *Rimozione.* I tag scelti compaiono come gettoni raggruppati sopra l'elenco; ✕ toglie un tag, e un gruppo rimasto vuoto sparisce.
- **Eventi.** Sono alternativi tra loro, quindi in OR. Il conteggio di un evento è il numero di foto che ha tra quelle che passano gli altri filtri.
- **Persone.** Sono in AND, come i gettoni (P14). Il conteggio è il numero di foto del risultato in cui la persona compare.

I conteggi si calcolano in SQL sull'intero risultato, non solo sulla parte mostrata nella griglia. Due indici coprenti (migrazione 0018) li fanno leggere solo dall'indice. Su 60.000 foto sintetiche un aggiornamento del pannello richiede 0,42 s (0,44 s in OR): prima degli indici erano 5,4 s. Restano entro il limite di 1 s della ricerca (§10).

### Scorciatoie della revisione

| Tasto | Azione |
| --- | --- |
| Frecce | Navigazione |
| `P` / `X` | Tieni / scarta |
| `1`-`5` | Stelle |
| `C` | Confronta |
| `Z` | Zoom 100% |
| `Ctrl+Z` | Annulla |

Etichettatura: `1` buona, `2` mossa, `3` occhi chiusi, `4` esposizione, `5` altro, `Spazio` salta.

### Flussi da rappresentare

| Flusso | Percorso |
| --- | --- |
| Import dopo un evento | Home (scheda rilevata) → Import → Job → Revisione → Persone (solo nuovi gruppi) |
| Primo passaggio sull'archivio | Librerie → Job in background → Persone (gruppi più grandi) → Vocabolario |
| Ricerca quotidiana | Consultazione → dettaglio → Apri in Photoshop |

Output atteso: sistema visivo, una tavola per schermata (stato tipico + stato vuoto + stato di errore dove rilevante), note su componenti non standard per Avalonia.

## 12bis. Esito del design (v0.1, 12 ott 2026)

Design ha prodotto il sistema visivo e le 11 schermate in cinque tavole HTML, da conservare in `docs/design/` del repository; questa sezione riporta i valori vincolanti per l'implementazione.

### Tavole

| File | Tavole | Contenuto |
| --- | --- | --- |
| `PhotoFlow_Sistema_Visivo_dc.html` | 01-08 | Colori, tipografia, spaziature, struttura finestra, componenti, tema chiaro, testi IT/EN, note Avalonia |
| `PhotoFlow_Revisione_dc.html` | 2a-2h | Griglia per raffiche (interattiva), Confronto, Numeri, Etichettatura, attesa, vuoto, conferma XMP, errore parziale |
| `PhotoFlow_Home_Import_Job_dc.html` | 3a-3i | Home (scheda rilevata, primo avvio, ripresa dopo crash), Import passi 1-3, Job (parallelo, vuoto, pausa per destinazione scollegata) |
| `PhotoFlow_Persone_Consultazione_Duplicati_dc.html` | 4a-4g | Persone (nuovi gruppi, pagina persona, vuoto, raggruppamento in corso, chiave non disponibile), Consultazione (ricerca mista, nessun risultato, indice in costruzione), Duplicati |
| `PhotoFlow_Librerie_Vocabolario_Profili_Impostazioni_dc.html` | 5a-5j | Librerie e rimappatura, Vocabolario (nucleo, personalizzati), Profili, Impostazioni (Hardware, Modelli, Catalogo, Generali, Culling) |

### Colori · tema scuro (default) e chiaro

| Ruolo | Scuro | Chiaro |
| --- | --- | --- |
| Visore | #0E0E0E | #2A2A2A (resta scuro) |
| Area griglia | #151515 | #DCDCDC |
| Finestra, barra laterale | #1B1B1B | #F2F2F2 |
| Pannelli | #222222 | #FAFAFA |
| Controlli | #2B2B2B | #E8E8E8 |
| Hover, premuto | #363636 | #DEDEDE |
| Bordo / bordo forte | #303030 / #4A4A4A | #D4D4D4 / #ABABAB |
| Testo primario / secondario / terziario | #EDEDED / #A6A6A6 / #808080 | #1A1A1A / #4F4F4F / #6B6B6B |
| Accento (selezione, focus, link) | #5E9EEA | #1F6FD1 |
| Azione primaria | #E6E6E6 (neutro) | #1A1A1A |

### Esiti del culling

| Esito | Scuro | Chiaro | Glifo | Miniatura |
| --- | --- | --- | --- | --- |
| Scarto | #EB5A50 | #C8372D | ✕ | Foto attenuata al 35% |
| Da rivedere | #E9B13C | #A86F00 | ? | Cornice tratteggiata 2 px |
| Migliore | #4CC38A | #1E8A55 | ★ | Cornice piena 2 px |
| Simile | #9C9AE0 | #5F5BB8 | ≈ | Solo badge |
| Tenuta | #3A3A3A | #D4D4D4 | ✓ | Badge neutro al passaggio o con filtro |

Ogni esito è riconoscibile anche senza colore (glifo + trattamento). Stati di sistema (informazione, avviso, errore) con forme distinte, mai usati come esito sulle miniature.

### Tipografia

IBM Plex Sans + IBM Plex Mono (OFL, incorporati come risorse), cifre tabulari ovunque cambino numeri.

| Stile | Dimensione/interlinea · peso |
| --- | --- |
| Titolo pagina | 24/32 · 600 |
| Titolo sezione | 18/24 · 600 |
| Sottotitolo | 15/22 · 500 |
| Corpo (base) | 13/20 · 400 |
| Etichetta | 12/16 · 500 |
| Dati (mono) | 12/16 · 400 |
| Micro (mono) | 11/14 · 500 |

Formati localizzati: 1.248 · 2,8 GB · 12 ott 2026 (IT); 1,248 · 2.8 GB · Oct 12, 2026 (EN).

### Spaziature, raggi, elevazione, movimento

| Elemento | Valore |
| --- | --- |
| Scala | Base 4: 4 · 8 · 12 · 16 · 24 · 32 · 48 |
| Raggi | 2 foto · 4 controlli · 8 dialoghi · cerchio per le persone |
| Elevazione | Due livelli; separazione con tono e bordo 1 px; un'unica ombra per dialoghi, menu, tooltip: 0 8 24 #80000000 |
| Animazioni | Solo transizioni ≤ 120 ms su opacità e colore; nessuno shimmer, nessuna sfocatura |

### Struttura della finestra

| Area | Dimensione |
| --- | --- |
| Barra del titolo (con "Cerca ovunque" Ctrl K) | 32 |
| Navigazione | 184, comprimibile a 48 (automatica a 1280 px) |
| Barra strumenti | 40 |
| Pannello dettagli | 300 (Tab per chiuderlo) |
| Barra di stato | 24, con i tasti del contesto corrente |
| Miniature | Celle quadrate S 120 · M 168 · L 240, gap 4, Ctrl +/− |
| Pagine impostazioni | Colonna max 760, etichetta sopra il controllo |

Testi elastici: pulsanti e chip crescono col testo (minimo 72 px); le barre strumenti spostano le voci in "Altro", mai a capo; nomi file e percorsi troncati al centro con tooltip.

### Componenti chiave

| Componente | Regole |
| --- | --- |
| Miniatura | In alto a sinistra esito + "M" se manuale; in alto a destra posizione nella raffica o selezione; in basso a sinistra stelle o nome file; in basso a destra motivo breve, RAW+JPG, XMP in sospeso. Anello blu = cursore |
| Chip tag | Pieno = manuale; contorno + confidenza = automatico; "Categoria ›" = gerarchico; tratteggio + EN = non rivisto; barrato = soppresso |
| Chip persona | Nome certo pieno; proposta sotto soglia tratteggiata amber con somiglianza |
| Barra fasi | 9 segmenti: completata, in corso, partita in anticipo, richiede l'utente |
| Ricerca | Campo unico a gettoni (tag, persone, eventi) + testo residuo = linguaggio naturale; tipo sempre dichiarato |
| Dialoghi | Titolo = domanda con numero esatto; primario ripete il verbo; azioni pesanti con contorno rosso, mai default su Invio |
| Stati | Vuoto con azione; attesa con celle statiche e contatore in barra di stato; errore come banner: causa, conseguenza, azione |

### Mappa Avalonia

| Elemento | Tipo | Soluzione |
| --- | --- | --- |
| Tema e token | Standard | ResourceDictionary con ThemeVariant su FluentTheme; niente Acrylic/Mica |
| Griglia miniature 60.000 | Personalizzato | ItemsRepeater con layout a righe virtualizzato; intestazioni di gruppo sticky; navigazione ↑↓ calcolata dal layout |
| Cornice tratteggiata | Template | Rectangle con StrokeDashArray |
| Confronto sincronizzato | Personalizzato | 2-4 ZoomBorder (PanAndZoom) con matrice condivisa; decodifica a tasselli solo al 100% |
| Ricerca a gettoni | Personalizzato | TextBox + WrapPanel di chip + Popup |
| Cursore a due maniglie (ISO) | Personalizzato | Nessun RangeSlider nativo |
| Selezione a rettangolo (Persone) | Personalizzato | Su ItemsRepeater |
| Vocabolario | Standard | DataGrid (pacchetto Avalonia) virtualizzato |
| Log del job | Standard | ListBox virtualizzato o AvaloniaEdit in sola lettura |
| Barra del titolo | Standard | ExtendClientAreaToDecorationsHint con pulsanti di sistema |
| Tasti | Standard | KeyBindings per vista; barra di stato generata dalla stessa tabella dei comandi |
| Dialoghi | Standard | OverlayLayer/DialogHost, velo nero 60% senza sfocatura |
| Notifica di sistema in background | Personalizzato, facoltativo | Integrazione di piattaforma |

### Scorciatoie globali e per vista

| Contesto | Tasti |
| --- | --- |
| Globali | Ctrl 1-7 sezioni · Ctrl K cerca · Ctrl I importa cartella · Ctrl , impostazioni · Ctrl Z annulla |
| Revisione | G/C/N/L viste · ← → ↑ ↓ · P tieni · X scarta · 1-5 stelle · Z 100% · F adatta · S sincronizza · Tab pannello · Maiusc R rianalizza gruppo · Ctrl R riprova XMP · Ctrl ↵ conferma e scrivi |
| Numeri | 0-9 digita numero · ↵ conferma e avanti · Canc nessun numero |
| Etichettatura | 1 buona · 2 mossa · 3 occhi chiusi · 4 esposizione · 5 altro · Spazio salta |
| Persone | F2 rinomina · M unisci · I ignora · S separa · Canc "non è lei/lui" |
| Consultazione | Ctrl E Photoshop · Ctrl ↵ app predefinita · Ctrl Maiusc E Esplora risorse · T tag · S trova simili |
| Import | ↵ avanti · Alt ← indietro · Ctrl ↵ avvia subito con le impostazioni ricordate |

Le cifre cambiano significato per vista (stelle, numero, etichetta) e la barra di stato lo dichiara sempre.

Le scorciatoie di una vista sono legate alla vista, non alla finestra: le lettere (P, X, cifre) non agiscono mentre si scrive in un campo di testo come la ricerca. In fase 1 `0` toglie le stelle e Ctrl Z annulla le decisioni della Revisione (l'annullamento globale arriva con le altre operazioni annullabili).

## 13. Guida per Claude Code

Sviluppo per fasi, ognuna chiusa solo quando tutti i suoi criteri di accettazione sono soddisfatti; questa specifica vive nel repository in Markdown ed è il riferimento per ogni sessione.

### Regole di lavoro

| Regola | Dettaglio |
| --- | --- |
| Fonte di verità | Questo documento; una modifica a una decisione si propone, non si applica in silenzio |
| Invarianti prima | I test delle invarianti (sezione 10) si scrivono prima del codice che le riguarda |
| Percorsi | Solo tramite `IAppPaths` |
| Dipendenze | Ogni libreria esterna dietro un'interfaccia di `Core` |
| Modelli | Mai nel repository; solo manifest con URL e SHA-256 |
| Dati personali | Foto di test fuori dalla cartella del repository; controllo pre-commit che blocca immagini non autorizzate |
| Avalonia vs design | Se una soluzione del design non è realizzabile fedelmente, segnalarlo invece di approssimare |
| Build | Esecuzione dalla cartella di build in sviluppo; pacchetto Velopack solo alle versioni di riferimento |

### Fasi

| Fase | Contenuto | Dipende da |
| --- | --- | --- |
| 0 | Repository, struttura della soluzione, CI, script Python di benchmark ed export ONNX (D-FINE, verifica RAM++), test di parità di base | — |
| 1 | Sorgenti SD + cartella; gestori RAW/JPG; ingest con copia verificata; esposizione/nitidezza; rilevamento volti e occhi; politiche XMP; catalogo base; procedura di import e riepilogo con griglia; avanzamento per foto (`JobItems`) e ripresa dei job di import dopo chiusura forzata | 0 |
| 1b | Gestori TIFF/PNG/HEIC; estensione della ripresa ai job d'archivio in background (`AnalyzeInPlace`), più job in parallelo, pausa/ripresa; bassa priorità; hash rapido/completo | 1 |
| 2 | Embedding CLIP; raffiche e sequenze speciali; schermata Revisione completa; riconoscimento volti, clustering, schermata Persone; cifratura impronte; modalità etichettatura | 1 |
| 3 | Tagging A+B; vocabolario e traduzioni; Consultazione con ricerca FTS e linguaggio naturale; Duplicati | 2 |
| 4 | OCR, elenco iscritti, profilo Motorsport, vista "Numeri" | 3 |
| 5 | Rigenerazione completa con anteprima differenze; didascalie (C/D); esportazione/importazione catalogo; sincronizzazione XMP esterna | 3 |

Le parti senza interfaccia della fase 1 (ingest, metadati, analisi, catalogo) possono partire in parallelo al lavoro di Design.

### Primissima attività della fase 1

Verifica sui primi NEF della Z5 II: risoluzione dell'anteprima incorporata, decodificabilità dei NEF High Efficiency, disponibilità del punto AF tramite exiftool. **Rinviata** (decisione F1-1 del 12 ott 2026): i NEF della Z5 II non sono disponibili prima dell'implementazione; il gestore RAW nasce generico (provato su D40/D5100 e RAW pubblici, NEF HE come solo anteprima) e la verifica diventa una checklist manuale dell'utente; se esistono NEF Z5 II pubblici con licenza chiara, si usano per un controllo anticipato.

### Interfaccia nelle fasi

Ogni fase realizza il layout completo delle tavole che la riguardano; i controlli di funzioni di fasi successive sono presenti ma disattivati, con il suggerimento "Arriva con la fase N" (decisione F1-6 del 12 ott 2026). La logica arriva solo nella fase prevista.

### Criteri di accettazione

**Fase 0**

- [ ] Soluzione compilabile con tutti i progetti e le interfacce di `Core`
- [ ] Benchmark Python eseguibile su una cartella di foto, con report dei tempi per fase
- [ ] D-FINE esportato in ONNX con test di parità superato

**Fase 1**

- [ ] Import di 1.000 NEF: zero file persi, tutti verificati
- [ ] Secondo import della stessa scheda: nessun duplicato
- [ ] Chiusura forzata a metà: ripresa dal punto di interruzione
- [ ] SD in sola lettura: nessun tentativo di scrittura o cancellazione (hash sorgente invariati)
- [ ] RAW invariati dopo la scrittura XMP
- [ ] XMP riletto correttamente da exiftool; checklist manuale in Esplora risorse e Photoshop/Camera Raw superata
- [ ] Set di foto mosse/occhi chiusi: percentuale di intercettazione misurata e riportata

**Fase 1b**

- [ ] Analisi di 10.000 foto miste interrotta e ripresa senza ripetere il lavoro
- [ ] File con estensione sbagliata gestiti dal gestore corretto
- [ ] File corrotti nel report, intatti

**Fase 2**

- [ ] Bracketing e timelapse mai classificati come raffiche nel set di test
- [ ] Revisione interamente utilizzabile da tastiera
- [ ] Chiave delle impronte rimossa: ricalcolo riuscito con assegnazioni preservate
- [ ] Metriche del culling calcolate dal set etichettato con margine di incertezza

**Fase 3**

- [ ] Ricerca < 1 s su 60.000 foto
- [ ] Keyword IT+EN e gerarchiche lette da Bridge
- [ ] Rianalisi: tag manuali e soppressi preservati

**Fase 4**

- [ ] Numeri riconosciuti solo se presenti in elenco; casi ambigui in "da rivedere"
- [ ] Equipaggi: solo numero nell'XMP

**Fase 5**

- [ ] Cambio modello volti: assegnazioni trasferite, nessuna persona persa
- [ ] Esportazione e importazione su altro percorso con rimappatura riuscita
- [ ] Modifiche XMP fatte in Bridge importate come manuali

## 14. Strategia di test

Sei livelli di test; quelli che usano foto personali girano solo in locale, e il repository pubblico funziona anche senza.

### Livelli

| Livello | Verifica | Esempi |
| --- | --- | --- |
| Unitari | Logica pura | Regole di decisione, raffiche, template percorsi, unione XMP |
| Parità | C# = modello originale Python | Embedding: somiglianza ≥ 0,999; tag: stesso insieme alle soglie ufficiali; riquadri: su due livelli (vedi sotto) |
| Integrazione | Componenti reali | exiftool, SQLite, "SD" simulata da cartella |
| Scenari | Condizioni avverse | 1.000 file; processo terminato a metà; NAS che sparisce; SD in sola lettura |
| Qualità | Metriche del culling (sezione 6) | Dal set etichettato |
| Prestazioni | Velocità per fase su GPU e CPU | Regressione oltre soglia rispetto alla versione precedente |

### Parità dei rilevatori (decisione del 12 ott 2026)

| Livello | Ingresso | Criterio |
| --- | --- | --- |
| Tensori | Tensore preparato da Python (float32 + JSON delle forme), identico per C# | Le 300 query vengono prima abbinate (assegnazione ottima su riquadri e punteggi), perché query quasi a pari merito possono scambiarsi di posizione tra runtime diversi; poi \|Δ punteggio (sigmoide)\| ≤ 5e-3 e \|Δ riquadri\| ≤ 5e-4 (decisione del 12 ott 2026; misurati su 40 foto reali: 2,7e-3 e 1,5e-4) |
| Rilevamenti | Immagine con pre-elaborazione C# | Abbinamento greedy per classe sopra soglia (0,5): ogni coppia IoU ≥ 0,95 e \|Δ punteggio\| ≤ 0,02; rilevamenti senza corrispettivo ammessi solo entro ±0,05 dalla soglia |

Riferimento: modello originale PyTorch con il preprocessore ufficiale. Immagini e riferimenti fuori dal repository (`PHOTOFLOW_TESTDATA`), report per immagine.

Volti buffalo\_l (tappa 1.7, 12 ott 2026): riferimento insightface 2.1 (`tools/lab/face_refs.py`, ambiente separato). Gli ingressi di SCRFD (letterbox 640) e di 2d106det (ritaglio 1,5× il volto, 192 px) si preparano con ricampionamento antialias (semantica Pillow, anche con riquadro frazionario) invece di `cv2.resize`/`cv2.warpAffine`, che campionano senza filtro quando riducono anteprime grandi; il ritaglio è allineato alla convenzione dei centri pixel di `warpAffine`. Il riferimento usa la stessa preparazione e il codice insightface per modello e post-elaborazione. Criteri: ingressi identici bit a bit; tensori \|Δ punteggi\| ≤ 5e-3, \|Δ distanze\| ≤ 5e-3 (unità di stride), \|Δ landmark grezzi\| ≤ 5e-4; rilevamenti IoU ≥ 0,95, \|Δ punteggio\| ≤ 0,02, landmark ≤ 2e-3 del lato del volto, rapporto di apertura degli occhi ≤ 5e-3. Misurati su 40 foto reali (265 volti): CPU identica (Δ = 0), CUDA Δ punteggi 5e-7, landmark 4e-4 del volto. Differenza tra le due preparazioni (informativa, volti ≥ 80 px): landmark 0,35% del volto (mediana), rapporto occhi 0,005 (mediana). YuNet 2023mar (alternativa con licenza MIT): ingresso fisso 640×640 riempito come SCRFD, decodifica e NMS come `FaceDetectorYN` di OpenCV 5.0; su 40 foto (247 volti) IoU ≥ 0,99999, \|Δ punteggio\| ≤ 6e-7, CPU e CUDA.

### Test delle invarianti

| Invariante | Verifica |
| --- | --- |
| Nessuna scrittura sulla sorgente | Hash di tutti i file prima e dopo ogni job |
| Nessuna cancellazione | Conteggio e confronto dei file |
| RAW mai modificati | Hash dei RAW prima e dopo la scrittura XMP |
| Nessuna fase prima della verifica | Iniezione di errori nella copia |

### Dati di test

| Set | Contenuto | Dove vive |
| --- | --- | --- |
| Formati | RAW di varie marche, TIFF 16 bit e multipagina, HEIC, PNG, file corrotti, estensioni sbagliate, foto senza EXIF | Repository pubblico (solo file con licenza libera verificata) |
| Subset d'archivio | 1.000-2.000 foto varie dell'archivio esistente, non etichettate: benchmark, formati, frequenza tag, comportamento clustering | Solo locale |
| Qualità | Foto etichettate dall'utente (obiettivo 300-500); in fase 1 cartelle `quality/buone`, `quality/mosse`, `quality/occhi_chiusi` in `PHOTOFLOW_TESTDATA` (decisione F1-7) | Solo locale |
| Riferimenti di parità | Output dei modelli originali | Generati in locale, versionati con il modello |

### Set di qualità ed etichettatura

Il set etichettato arriverà dopo l'inizio dell'uso: fino ad allora vale la modalità prudente (D20). Le correzioni in revisione alimentano il set ma sono distorte (le foto non controllate non producono etichette), quindi serve anche etichettatura esplicita.

| Funzione della modalità etichettatura | Specifica |
| --- | --- |
| Un tasto per foto | `1` buona, `2` mossa, `3` occhi chiusi, `4` esposizione, `5` altro, `Spazio` salta |
| Raffiche | Clic sulla migliore + conferma |
| Selezione intelligente | Prima le foto più utili: vicine alle soglie, incerte, profili diversi |
| Sessioni brevi | \~100 foto (\~15 min), interrompibili |
| Alla cieca | Verdetto del sistema nascosto (default per il set di misura) |
| Pannello metriche | Valori con margine di incertezza |

### Compatibilità XMP

Scrittura → rilettura con exiftool a ogni build; checklist manuale in Esplora risorse, Bridge, Photoshop/Camera Raw (e Lightroom se adottato) a ogni versione di riferimento.

## 15. Inventario licenze

Il progetto è Apache 2.0; l'unico componente non commerciale (buffalo\_l) è opzionale, separato e scaricato con accettazione esplicita.

### Librerie

| Componente | Licenza | Stato |
| --- | --- | --- |
| .NET, Avalonia, CommunityToolkit.Mvvm, SkiaSharp | MIT | Verificato |
| ONNX Runtime, ONNX Runtime GenAI | MIT | Verificato |
| DirectML (`Microsoft.AI.DirectML`), runtime Windows ML | Licenze proprietarie Microsoft, ridistribuibili a condizioni; i provider di vendor di Windows ML hanno licenze proprie | Verificato 12 ott 2026 |
| Microsoft.Data.Sqlite / SQLite | MIT / pubblico dominio | Verificato |
| Dapper, Serilog, xUnit | Apache 2.0 | Verificato |
| OpenCvSharp | Apache 2.0; non adottato (F1-4) | Verificato 12 ott 2026 |
| Magick.NET (15.0.0) | Apache 2.0; libheif, libde265 (LGPL) e LibRaw **collegate staticamente** in Magick.Native: serve voce NOTICE, offerta dei sorgenti LGPL e revisione di conformità (R19) | Verificato 12 ott 2026 |
| exiftool | Licenza di Perl (Artistic/GPL), ridistribuibile | Da conoscenza |
| Velopack / vpk (1.2.161) | MIT; canali per variante, feed statico, ritorno indietro con `AllowVersionDowngrade` | Verificato 12 ott 2026 |
| [Florence2-Sharp](https://docs.curiosity.ai/florence2-sharp/) | MIT; non adottato (F5-1: adattatori nostri su `IInferenceProvider`) | Verificato |
| [RapidOcrNet](https://www.nuget.org/packages/rapidocrnet) (4.2.0) | Codice Apache 2.0; non adottato (F4-1: adattatori nostri sui modelli RapidOCR, per usare `IInferenceProvider`) | Verificato 12 ott 2026 (codice) |
| [HdbscanSharp](https://www.nuget.org/packages/HdbscanSharp) (3.0.1) | MIT; inattivo da maggio 2025; O(n²), non adatto a ~100.000 volti (R18) | Verificato 12 ott 2026 |
| IBM Plex Sans / Plex Mono (font) | SIL Open Font License, incorporabile | Da design |
| PanAndZoom (pacchetto `PanAndZoom` ≥ 12.1.1, Avalonia 12) | MIT; `ZoomBorder` con `SetMatrix` / `MatrixChanged` per il confronto sincronizzato | Verificato 12 ott 2026 |
| Avalonia DataGrid, AvaloniaEdit (opzionale) | MIT | Da verificare |
| VirtualizingWrapPanel di terze parti (alternativa a ItemsRepeater) | Da verificare | Aperto, solo se adottato |

### Modelli

| Modello | Licenza | Uso commerciale |
| --- | --- | --- |
| [D-FINE](https://pypi.org/project/easydetect/) | Apache 2.0 (codice e pesi) | Sì |
| [RF-DETR](https://pypi.org/project/rfdetr-onnx/) Nano-Large | Apache 2.0 (XLarge/2XLarge: PML 1.0, esclusi) | Sì |
| [CLIP](https://huggingface.co/Xenova/clip-vit-base-patch32) | MIT (la scheda del modello OpenAI dichiara fuori ambito gli usi "deployed": indicazione d'uso, non licenza; da valutare in fase 2) | Sì |
| [RAM++ (ONNX int8)](https://huggingface.co/anakhiu/ram-plus-onnx-int8) | Apache 2.0 (provenienza debole; non supera la parità, vedi §5) | Sì |
| [Florence-2](https://huggingface.co/microsoft/Florence-2-base-ft) (base-ft: `onnx-community` e nostro export dell'encoder visivo da `florence-community`) | MIT (verificato il 13 ott 2026) | Sì |
| PaddleOCR (modelli ONNX di `SWHL/RapidOCR`) | Apache 2.0 (repository RapidOCR e PaddleOCR) | Sì |
| Qwen2.5-VL | 3B: "qwen-research" (non commerciale); 7B: Apache-2.0 (schede Hugging Face, verificate il 13 ott 2026) | 3B no; 7B sì |
| Qwen3.5-9B | Apache-2.0, repository non soggetto ad accettazione (scheda e API di Hugging Face, verificate il 15 ott 2026) | Sì |
| Predittore estetico LAION | Da verificare | Da verificare |
| [InsightFace buffalo\_l](https://pypi.org/project/insightface/) | Solo ricerca non commerciale | No |
| YuNet (OpenCV Zoo) | MIT (LICENSE in opencv\_zoo, verificato il 12 ott 2026) | Sì |
| SFace (OpenCV Zoo) | Apache 2.0 (LICENSE in opencv\_zoo, verificato il 12 ott 2026) | Sì |
| AdaFace IR-101 WebFace12M (CVLFace) | Codice AdaFace MIT; la scheda dei pesi rimanda alla licenza del set di addestramento (WebFace, termini non verificabili il 15 ott 2026) | Trattato come non commerciale, con accettazione |
| YOLO Ultralytics | AGPL-3.0 | Escluso dal progetto |

## 16. Registro dei rischi

Il rischio principale è l'allargamento continuo dell'ambito; questa specifica e il controllo delle modifiche sono la mitigazione.

| # | Rischio | Prob. | Impatto | Mitigazione |
| --- | --- | --- | --- | --- |
| R1 | Allargamento continuo dell'ambito | Alta | Alto | Fasi con criteri di chiusura; nuove idee nel backlog salvo che cambino le fondamenta |
| R2 | Culling poco affidabile | Media | Alto | Modalità prudente, metriche misurate, invarianti |
| R3 | Anteprime Z5 II non a piena risoluzione o NEF High Efficiency non decodificabili | Media | Medio | Verifica come prima attività della fase 1; fallback di decodifica. **Chiuso il 14 ott 2026**: anteprima della Z5 II a piena risoluzione (6048×4032); NEF HE gestiti dall'anteprima (R15) |
| R4 | Export ONNX non fedele (RAM++, D-FINE) | Bassa | Medio | Test di parità; alternative CLIP zero-shot e RF-DETR |
| R5 | Futuro di DirectML (spinta verso Windows ML) | Chiuso | — | DirectML escluso (F1-2); Windows ML nel backlog con la parità come condizione |
| R6 | Librerie piccole abbandonate | Media | Medio-basso | Interfacce; sostituzione o fork |
| R7 | Corruzione del catalogo | Bassa | Alto | Rollback in rete, lock, backup, integrità |
| R8 | Prestazioni insufficienti su sola CPU | Media | Basso | Livelli di tagging, esecuzione notturna |
| R9 | Foto personali nel repository pubblico | Bassa | Alto | Dati fuori dal repository, controllo pre-commit |
| R10 | Incompatibilità XMP con i software | Media | Medio | Politiche configurabili, checklist manuale |
| R11 | Errori del riconoscimento facciale | Media | Medio | Soglie che preferiscono dividere, revisione, campioni aggiornati |
| R12 | Cambio di licenza di un modello | Bassa | Medio | Versioni fissate con SHA-256, licenza registrata |
| R13 | exiftool bloccato o lento | Bassa | Medio | `-stay_open`, controllo di salute, riavvio automatico |
| R14 | Durata del progetto con impegno serale | Alta | Medio | Ogni fase produce un applicativo utilizzabile |
| R15 | NEF High Efficiency (HE/HE*) non decodificabili da LibRaw pubblica (0.22.2); Z5 II non in elenco | Alta | Medio | Anteprima incorporata via exiftool, stato "RAW HE: solo anteprima"; aggiornare allo snapshot LibRaw annunciato per l'autunno 2026. Verificato sui NEF della Z5 II (14 ott 2026): anteprima a piena risoluzione; LibRaw restituisce un'immagine sbagliata senza errore, quindi i NEF HE non le vengono mai passati |
| R16 | Una sola distribuzione di ONNX Runtime per processo | Bassa | Medio | Scelto ORT 1.31 (CUDA + CPU, F1-2): compatibile con RapidOcrNet (≥ 1.29); ogni nuovo backend o libreria con ORT propria va verificato |
| R17 | CUDA in migrazione verso un plugin EP di ONNX Runtime; dimensione del pacchetto GPU (CUDA/cuDNN ~1-1,5 GB) | Media | Medio | `IInferenceProvider`; pacchetto GPU separato e facoltativo, con SHA-256; CPU sempre disponibile |
| R18 | Clustering volti: HdbscanSharp O(n²) non scala a ~100.000 volti | Alta | Medio | HDBSCAN su grafo k-NN dietro interfaccia di Core, validato contro Python in `tools/lab` |
| R19 | LGPL (libheif, libde265, LibRaw) collegate staticamente in Magick.Native | Media | Medio | NOTICE e offerta dei sorgenti; revisione di conformità prima della prima versione distribuita. **Fatta per la 0.1.0 (13 ott 2026)**: Magick.Native è ridistribuita non modificata come libreria condivisa separata e sostituibile; nel pacchetto `licenses/` con le note e i testi di licenza di Magick.NET; elenco delle librerie LGPL con versioni e sorgenti, offerta scritta dei sorgenti in `THIRD-PARTY-NOTICES.md`. Non è un parere legale; i brevetti HEVC (HEIC) restano un tema separato, mitigabile disattivando HEIC |
| R20 | Perl di sistema deprecato su macOS (serve a exiftool) | Media | Basso | Controllo all'avvio con messaggio chiaro; in prospettiva Perl incluso nel pacchetto Mac |

## 17. Backlog e punti da verificare

Decisioni emerse in Design (confermate), funzioni rinviate a dopo la stabilizzazione e verifiche tecniche da fare durante lo sviluppo.

### Decisioni emerse in Design (confermate il 12 ott 2026)

| # | Area | Decisione proposta | Nota |
| --- | --- | --- | --- |
| P1 | Revisione | P su una "Migliore" la conferma; su qualsiasi altra la rende "Tenuta" manuale. P nel Confronto = "questa è la migliore": le altre valide diventano Simili | Semantica di P diversa tra Griglia e Confronto |
| P2 | Revisione | Durante l'analisi P/X/stelle sono già accettati e prevalgono sulla proposta successiva | Coerente con "le azioni dell'utente prevalgono" |
| P3 | Revisione | Riaprire un job confermato è consentito: le modifiche riscrivono solo gli XMP interessati |  |
| P4 | XMP | Errore di scrittura (es. cartella in sola lettura): decisioni salvate nel catalogo, scrittura in sospeso con nuovo tentativo all'avvio, oppure "solo catalogo per questi" | Gli errori non bloccano il resto |
| P5 | Duplicati | Le copie non tenute si spostano in `_duplicati` nella stessa libreria, mai eliminate; gruppi "non duplicati" esclusi in seguito | Non applicabile a librerie in sola lettura |
| P6 | Revisione | Azione "Sposta scarti…" in una cartella dedicata, su richiesta esplicita | Coerente con I4 |
| P7 | Job | L'import ha precedenza sulla GPU: i job d'archivio rallentano mentre un import è in corso | Nuova regola di scheduling |
| P8 | Job | Destinazione scollegata durante la copia: job in pausa, con "Riprendi" o "Continua senza" quella destinazione |  |
| P9 | Import | Opzione "Tutte, incluse le già importate" oltre a "Solo nuove" (default) | Regola: le già importate presenti e integre nella destinazione (hash uguale) non vengono ricopiate; quelle mancanti o diverse vengono ricopiate e verificate. Mai sovrascritture |
| P10 | Import | Data dell'evento modificabile, proposta dagli scatti |  |
| P11 | Primo avvio | Procedura in 3 passi: hardware e benchmark, modelli con scelta della licenza volti (buffalo\_l o YuNet/SFace), prima libreria | La scelta YuNet/SFace dichiara che gli occhi chiusi non vengono rilevati |
| P12 | Persone | Campioni di riferimento per anno, rinnovati automaticamente con i volti confermati; ordinamento "prima i meno simili" nella pagina persona |  |
| P13 | Persone | Chiave impronte non disponibile: galleria consultabile, riconoscimento sospeso fino a password o ricalcolo | Coerente con D12 |
| P14 | Consultazione | Persone e tag sono filtri esatti; il testo libero ordina per significato; azione "Trova simili" |  |
| P15 | Vocabolario | Il nucleo si forma dopo almeno 1.000 foto con tag |  |
| P16 | Tagging | Didascalie VLM (livello D) disponibili solo con GPU NVIDIA ≥ 12 GB | Rivista il 13 ott 2026 su indicazione dell'utente (era ≥ 8 GB): misurato Qwen2.5-VL-7B int4 da solo con picco ~10 GB (pesi 6,5 GB, ONNX Runtime GenAI, generazione), oltre a Windows e agli altri modelli. Confermata il 15 ott 2026 per Qwen3.5-9B: picco 11,7 GB con la tabella di embedding fp16 della ricetta, 9,8 GB con la tabella int8 adottata (RTX 3090, 12 foto a 1024 px) |
| P17 | Profili | Parametri esposti: scarto per mosso sotto soglia, intervallo massimo raffica (2,0 s), similarità minima (0,90), pari merito entro 0,02 | Valori iniziali da tarare |
| P18 | Impostazioni | Cambio lingua applicato al riavvio della finestra (non a caldo) | Semplifica la localizzazione |
| P19 | Catalogo | Estensione del file catalogo `.pfdb` |  |
| P20 | Import | Variabili del template percorsi: `{yyyy}` `{MM}` `{evento}` `{seq}` `{camera}` `{nomeoriginale}` `{ext}`; caratteri non validi sostituiti con "-" |  |
| P22 | Persone | Richiesta dell'utente (15 ott 2026): modello di riconoscimento scelto a parte (ArcFace, SFace, AdaFace IR-101 opzionale); rigenerazione dei volti su tutto il catalogo; opzione esplicita per ripartire da zero con le persone (persone, nomi, assegnazioni anche manuali, volti disegnati a mano, in tutto il catalogo, dopo conferma con i conteggi) — §7 | L'azzeramento è un'azione dell'utente, non una sovrascrittura automatica (I5) |

### Backlog post-stabilità

| Voce | Origine |
| --- | --- |
| Album (ed eventuale esportazione come keyword `Album\|Nome`) | D6 |
| Modalità portatile | D19 |
| Riconoscimento automatico dei panorami | Sezione 6 |
| Firma del codice | Sezione 11 |
| Mappa GPS | Sezione 1 |
| Catalogo su PostgreSQL (homelab) per uso multi-PC simultaneo | Sezione 9 |
| Backend GPU per schede non NVIDIA (Windows ML con provider di vendor, o DirectML se corretto): ammesso solo dopo i test di parità | F1-2 |
| Rivalutare OpenCvSharp se le fasi successive richiedono elaborazioni complesse | F1-4 |
| Punto AF delle reflex (punti con nome, senza coordinate): mappa delle posizioni per modello | Tappa 1.7 |
| Annulla globale (Ctrl Z) oltre le decisioni della Revisione | Tappa 1.9 |
| Profilo automatico: classificare la scena per foto con CLIP prima dell'analisi, così solo le foto il cui gruppo cambia profilo vanno rianalizzate (oggi +55% sul set locale) | Tappa 2.12 |
| Suggerimenti dalle correzioni anche per le soglie degli occhi e dell'esposizione | Tappa 2.10 |
| Persone: annulla (Ctrl Z) di unisci/separa/rinomina, selezione a rettangolo (tavole 4a, 4b); "Apri foto" fatto il 13 ott 2026 (↵ o doppio clic, foto intera con il riquadro del volto) | Tappa 2.8 |
| Modelli caricati e scaricati fase per fase (tavola 5f: "bastano 4-6 GB di VRAM"); oggi restano caricati per tutta la sessione | Tappa 2.11 |
| Portachiavi Mac/Linux per la chiave delle impronte (oggi password) | F2-3 |
| Eliminare un profilo personalizzato (oggi si può solo smettere di usarlo; i file restano nella cartella dei profili) | Tappa 2.10 |
| Duplicati: zoom sincronizzato nel confronto e "Mostra esclusi" (tavola 4f, 4g) | Tappa 3.7 |
| Consultazione: filtro "Evento ▾" a tendina (oggi l'evento è un gettone del campo di ricerca) e selezione multipla nella griglia | Tappa 3.6 |
| Vocabolario: DataGrid con modifica in cella (oggi elenco a colonne con modifica nel pannello) | Tappa 3.8 |
| Quando una keyword scritta da PhotoFlow c'era già nel file prima (scritta da un altro programma), toglierla solo se l'altro programma non la usa più: serve la sincronizzazione della fase 5 | Tappa 3.4 |
| Scritture del catalogo per foto in un'unica transazione (oggi ~11 commit per foto: il catalogo regge ~45 foto/s, oltre il bisogno con i modelli) | Tappa 1b.7 |
| Accelerazione SIMD del ricampionamento e dei segnali (oggi l'analisi è limitata dalla CPU: 8 foto in parallelo su 24 thread) | Tappa 1.10 |
| Cambio di politica XMP di una libreria: unione dei sidecar esistenti negli incorporati (o viceversa) | D21, fase 5 (sincronizzazione) |
| Vista Numeri: foto intera con i riquadri delle vetture (ambra = numero incerto, grigio = altra vettura riconosciuta), "Conferma e scrivi XMP" nella vista, numero di eventi in cui è usato un elenco (tavola 2c); oggi ritagli con le letture e conferma dalla Revisione | Tappa 4.5 |
| Elenco iscritti: modifica in tabella (oggi come testo, una vettura per riga) | Tappa 4.5 |
| Ricampionamento più veloce per l'OCR (oggi il ridimensionamento antialias del fotogramma costa ~65 ms a 1600 px) | Tappa 4.6 |
| Didascalie VLM dentro `IInferenceProvider` con test di parità esatta (oggi ONNX Runtime GenAI in un processo separato, eccezione U4) | Fase 5b |
| Livello D più vicino al modello originale: quantizzazione calibrata (AWQ/GPTQ) o fp16 sulle GPU da 24 GB (oggi int4 RTN: 0/10 didascalie identiche, 53% di token in comune con PyTorch) | Tappa 5b.5 |
| Livello D su GPU da 8-10 GB (oggi ≥ 12 GB: il 7B int4 arriva a ~10 GB) | Tappa 5b.5 |
| Eliminazione dei pesi originali del VLM dall'app dopo la conversione (oggi a mano dalla cartella) | Tappa 5b.2 |
| Didascalie più veloci: cache dell'encoder sulla GPU (IO binding) invece di ricopiarla a ogni passo del decoder; Florence-2 large come alternativa | Tappa 5.1 |
| Rigenerazione: scambio dell'indice embedding quando ci sarà un secondo modello di embedding | Tappa 5.4 |
| Registro della sincronizzazione XMP consultabile nell'interfaccia (oggi nel catalogo e nel riepilogo delle Impostazioni) | Tappa 5.6 |
| Scaricare dall'app i pesi originali del VLM (oggi a mano, U2): URL fissati a una revisione e SHA-256 sono già nel manifest; serve una conferma esplicita per i ~18 GB | Passaggio a Qwen3.5-9B, 15 ott 2026 |
| Qwen3.5-4B come alternativa per GPU da 8 GB (stessa ricetta; punteggi vicini al 9B secondo la scheda): permetterebbe di abbassare P16 | Passaggio a Qwen3.5-9B, 15 ott 2026 |
| Area AF dai punti usati (`AFPointsUsed`) quando la Z5 II non registra le coordinate (area automatica): serve la griglia dei punti AF del modello | NEF Z5 II, 14 ott 2026 |
| `FocusResult` di Nikon ("Out of Focus") come segnale in più per il culling | NEF Z5 II, 14 ott 2026 |

### Verifiche tecniche

- [x] Risoluzione dell'anteprima incorporata nei NEF della Z5 II (14 ott 2026, NEF forniti dall'utente: `JpgFromRaw` 6048×4032, a piena risoluzione rispetto ai 6064×4040 del sensore; anche `OtherImage` 1620×1080 e `PreviewImage` 640×424)
- [x] Decodifica dei NEF High Efficiency (LibRaw) e scelta del binding .NET di LibRaw (12 ott 2026: nessuna LibRaw pubblica decodifica HE/HE*, Z5 II non in elenco — R15; binding proposto: P/Invoke sottile sui binari ufficiali 0.22.x, Sdcb.LibRaw fermo a 0.21.1). **14 ott 2026**: la LibRaw di Magick.NET 15.0.0 "decodifica" i NEF HE* della Z5 II **senza errore ma in un'immagine sbagliata** (bande colorate); i NEF HE non arrivano più a LibRaw: si usa sempre l'anteprima incorporata, e senza anteprima il file è segnalato come non decodificabile. Binding di LibRaw non più necessario finché non esiste una LibRaw pubblica con TicoRAW
- [x] Punto AF Nikon leggibile con exiftool per la Z5 II (14 ott 2026: `AFAreaXPosition/YPosition/Width/Height` su `AFImageWidth/Height` quando `AFCoordinatesAvailable` = Yes; in area automatica senza coordinate solo `AFPointsUsed`, e PhotoFlow ripiega su soggetto e centro). exiftool chiama questi NEF "NRW": il formato registrato resta NEF
- [ ] Lettura dei sidecar/incorporati in Esplora risorse, Photoshop/Camera Raw, Bridge, Lightroom
- [x] Stato di Velopack e di DirectML rispetto a Windows ML (12 ott 2026: Velopack attivo; DirectML in sustained engineering, NuGet fermo a 1.24.4; Windows ML GA con ORT 1.27.1 — R16, R17)
- [ ] Licenze aperte: Qwen2.5-VL, predittore estetico (chiuse il 12 ott 2026: HdbscanSharp MIT, codice RapidOcrNet Apache 2.0, decoder HEIC in Magick.NET LGPL statici)
- [x] Perl disponibile sulle versioni recenti di macOS (12 ott 2026: presente su macOS 26 per evidenza indiretta, deprecato da Apple dal 2019 — R20)
- [ ] Riconoscimento di monoposto e prototipi da parte di D-FINE (COCO) sulle foto in pista
- [ ] Qualità di YuNet/SFace rispetto a buffalo\_l
- [x] Tempi reali con il benchmark (13 ott 2026: benchmark in-app nelle Impostazioni, tavola 5f; misure locali in `docs/reports/fase-2.md`)

### Fonti consultate

[RAM++ ONNX int8](https://huggingface.co/anakhiu/ram-plus-onnx-int8) · [RAM++ ONNX export](https://huggingface.co/benjaminjonard/ram-plus-onnx) · [Florence-2 ONNX](https://huggingface.co/onnx-community/Florence-2-large) · [Florence2-Sharp](https://docs.curiosity.ai/florence2-sharp/) · [InsightFace](https://pypi.org/project/insightface/) · [InsightFace model zoo](https://github.com/deepinsight/insightface/blob/e68b8b076fb11e9f75c87244d234385f0f318181/model_zoo/README.md) · [ONNX Runtime GenAI multimodale C#](https://mintlify.wiki/microsoft/onnxruntime-genai/examples/csharp/multimodal) · [Qwen3-VL in ORT GenAI](https://github.com/microsoft/onnxruntime-genai/issues/1989) · [RapidOcrNet](https://www.nuget.org/packages/rapidocrnet) · [HdbscanSharp](https://www.nuget.org/packages/HdbscanSharp) · [CLIP ONNX](https://huggingface.co/Xenova/clip-vit-base-patch32) · [SQLite over a network](https://sqlite.org/useovernet.html) · [RF-DETR](https://pypi.org/project/rfdetr-onnx/) · [D-FINE (easydetect)](https://pypi.org/project/easydetect/) · [D-FINE vs YOLO11](https://playground.roboflow.com/models/compare/d-fine-vs-yolo11)
