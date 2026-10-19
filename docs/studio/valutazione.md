# PhotoFlow Studio — valutazione dal punto di vista del fotografo

Data: 19 ottobre 2026 · versione valutata: 0.1.0 (commit `4c58718`)

Lo scenario: un fotografo rientra in studio dopo un servizio (un matrimonio, una gara, un evento aziendale) con
**circa 2.000 scatti RAW** su due schede, fatti con due corpi macchina. Qui sotto il flusso che quelle foto
dovrebbero seguire, il confronto con ciò che PhotoFlow Studio fa oggi, la valutazione e il piano di modifiche.

Le funzioni indicate come presenti o assenti sono state verificate nel codice della 0.1.0; i tempi sono stime da
misurare su RAW reali.

> **Stato (versione 0.2.0):** le ondate 1 e 2 (M1–M11) sono realizzate; le decisioni sono S14–S23 in
> [`progetto.md`](progetto.md). Restano le ondate 3–5 (M12–M24) e i temi trasversali.

## 1. Il flusso ideale delle 2.000 foto

| # | Fase | Cosa deve succedere | Perché |
|---|---|---|---|
| 1 | Scarico | Leggere entrambe le schede nello stesso evento, copiare tutto, verificare ogni file, rinominare con uno schema fisso | Le schede si riformattano e si riusano: la copia deve essere certa |
| 2 | Messa in sicurezza | Avere subito almeno due copie verificate (disco di lavoro + NAS o disco esterno), poi sapere che la scheda si può formattare | Regola 3-2-1; il rischio più alto è nelle prime ore |
| 3 | Metadati di base | Autore, copyright, contatti, evento, cliente, luogo; orari dei due corpi allineati | Senza orari allineati le due macchine si mescolano male; senza copyright le consegne escono anonime |
| 4 | Prima cernita (tecnica) | Togliere mosse, sfocate, occhi chiusi, esposizioni sbagliate, doppioni delle raffiche | Da 2.000 a ~1.200 senza guardarle una per una |
| 5 | Seconda cernita (scelta) | Confrontare le simili, scegliere la migliore di ogni momento, stelle e etichette colore | Da ~1.200 a 300–500 foto da consegnare |
| 6 | Selezione con il cliente (facoltativa) | Mandare una galleria di provini, ricevere le scelte | Matrimoni, ritratti, aziende |
| 7 | Sviluppo | Correzione di base per gruppi di luce simile, poi ritocchi foto per foto: ritaglio, raddrizzamento, esposizione, bilanciamento, rumore, nitidezza | È la fase più lunga: deve lavorare per gruppi, non foto per foto |
| 8 | Ritocco esterno | Poche foto in Photoshop, con il file che torna nel catalogo | Pelle, elementi da togliere, composizioni |
| 9 | Consegna | Più uscite: web con filigrana, alta risoluzione per il cliente, stampa, eventuale galleria o ZIP | Ogni destinatario vuole un formato diverso |
| 10 | Archiviazione | Spostare l'evento dal disco veloce all'archivio, tenere le copie, decidere cosa fare degli scarti, liberare spazio | Il disco di lavoro si riempie a ogni servizio |
| 11 | Ricerca e riuso | Mesi o anni dopo: ritrovare «la sposa con il velo al tramonto», «tutte le foto di Marco», «le foto a 200 mm a f/2.8 del 2025» | Portfolio, ristampe, clienti che tornano |

## 2. Confronto fase per fase

Legenda: **Sì** coperto bene · **Parziale** c'è ma manca qualcosa di importante · **No** assente.

| # | Fase | PhotoFlow Studio 0.1.0 | Copertura |
|---|---|---|---|
| 1 | Scarico | Scheda rilevata (DCIM) e proposta in Home; import in tre passi con copia verificata xxHash3, foto già importate riconosciute, coppie RAW+JPEG, ripresa dopo interruzione; schema dei percorsi con `{yyyy}` `{yyyy-MM-dd}` `{evento}` `{seq}` `{camera}` `{nomeoriginale}`. Manca un modo esplicito per aggiungere la seconda scheda allo stesso evento e allineare gli orari tra i corpi | Parziale |
| 2 | Messa in sicurezza | Destinazioni multiple nello stesso import (principale, backup, NAS), tutte verificate; la scheda non viene mai toccata. Non dice «scheda al sicuro, puoi formattarla» e non mostra quante copie esistono di un evento | Parziale |
| 3 | Metadati di base | Evento e data all'import; tag, persone e didascalie automatiche. **Nessun modello autore/copyright/contatti** (IPTC), nessun luogo, nessuno spostamento orario | No |
| 4 | Prima cernita | Punto di forza: nitidezza e mosso, esposizione, occhi chiusi, soggetto, raffiche, bracketing e timelapse raggruppati con proposta della migliore, profili di scena, modalità prudente, esiti spiegati | Sì |
| 5 | Seconda cernita | P/X, stelle 1–5, confronto 2–4 foto con zoom sincronizzato, foto grande, filtri per esito, tutto da tastiera, XMP per Lightroom. **Etichette colore non assegnabili** (solo il rosso automatico per «da rivedere»); nessuna azione su più foto insieme | Parziale |
| 6 | Selezione con il cliente | Nessun foglio provini, nessuna galleria, nessun ritorno delle scelte | No |
| 7 | Sviluppo | Luce, colore, curva, saturazione, contrasto; riduzione del rumore classica e neurale con preset automatico; «Applica alle selezionate»; regolazioni nel catalogo. **Mancano ritaglio, raddrizzamento e rotazione, nitidezza, correzioni dell'obiettivo**, «seleziona tutto», gruppi per luce simile, regolazioni locali | Parziale |
| 8 | Ritocco esterno | Ctrl E apre nell'editor esterno (Photoshop); il file salvato non viene ricollegato all'originale | Parziale |
| 9 | Consegna | Ricette Web, Cliente, Stampa, DNG per Lightroom, Personalizzata; coda in background, nomi con `{evento}` `{stelle}` `{seq}`, GPS tolto per il web. **Mancano filigrana, scelta dello spazio colore, nitidezza di uscita, ZIP o galleria** | Parziale |
| 10 | Archiviazione | Struttura per anno/data/evento, XMP accanto ai RAW, librerie, duplicati, scarti spostati in `_scarti` su richiesta, backup e esportazione del catalogo. **Nessuno spostamento guidato verso l'archivio, nessun controllo periodico dell'integrità**, cache delle miniature potata (i dischi scollegati restano poco consultabili) | Parziale |
| 11 | Ricerca e riuso | Persone, eventi, tag IT/EN con faccette e conteggi, ricerca a parole per significato, trova simili, date, fotocamera, stelle, esito. **Mancano obiettivo, focale, diaframma, tempo, ISO, luogo, cartella, formato, stato (sviluppata/esportata), etichetta, ricerche salvate e album** | Parziale |

## 3. Valutazione

### Copertura funzionale — 3 su 5

Il cuore del lavoro è coperto: scarico sicuro, cernita assistita (il pezzo più forte, raro anche nei programmi
commerciali), sviluppo con un'ottima riduzione del rumore, consegna con ricette, ricerca per persone e significato.
Mancano però tre cose che un fotografo usa a ogni servizio: **ritaglio e raddrizzamento**, **metadati di copyright**
e **etichette colore/selezioni multiple**. Senza ritaglio, oggi ogni foto storta va finita in un altro programma.

### Praticità esecutiva — 3 su 5

Il flusso è guidato bene (Revisione → «Sviluppa le migliori» → Esporta, tutto da tastiera, nessuna finestra modale,
coda in background). Con 2.000 foto emergono gli attriti:

- **Selezione singola in Consultazione** e nessun «seleziona tutto» nella striscia dello Sviluppo: ogni azione di
  gruppo passa per i pulsanti «Sviluppa le migliori/i risultati».
- **Sviluppo foto per foto**: «Applica alle selezionate» copia i campi cambiati, ma non ci sono gruppi per luce
  simile né un preset applicato a tutto l'evento come punto di partenza.
- **Riduzione del rumore neurale calcolata a richiesta**: con centinaia di foto ad alti ISO l'attesa si sposta
  all'esportazione, senza una stima del tempo totale.
- **Una scheda alla volta**: il secondo corpo macchina è un secondo import.

Tempi indicativi da misurare (2.000 RAW da ~25 MB, ~50 GB): copia e verifica su SSD 10–15 minuti, seconda copia su
NAS a 1 Gb/s altri 8–10 minuti, analisi completa da qualche decina di minuti con GPU NVIDIA a oltre un'ora su CPU.
La cernita umana resta il collo di bottiglia, ed è dove l'app aiuta di più.

### Archiviazione e ricerca — 3,5 su 5

L'ordine fisico è buono (cartelle per anno/data/evento con nomi coerenti, XMP standard leggibili da Lightroom e
Bridge, originali mai toccati, duplicati sotto controllo). La ricerca per persone, tag ed eventi, con faccette e testo
libero per significato, è sopra la media. Restano scoperti i criteri tecnici (obiettivo, focale, diaframma, ISO), il
**luogo**, lo **stato di lavorazione** (sviluppata, consegnata) e le **raccolte** (album manuali e ricerche
salvate). Sulla durata nel tempo mancano il controllo dell'integrità e lo spostamento guidato verso l'archivio.

### Altro da considerare

| Tema | Stato | Nota |
|---|---|---|
| Sicurezza dei dati | Buona | Mai cancellazioni automatiche, copie verificate, backup del catalogo a rotazione, XMP standard |
| Privacy | Ottima | Tutto in locale, nessun account, GPS tolto nelle esportazioni web |
| Interoperabilità con Lightroom | Buona | Stelle, parole chiave, persone e didascalie in XMP; DNG ripulito con istruzioni Camera Raw. Le etichette colore e i ritagli non viaggiano ancora |
| Gestione del lavoro e dei clienti | Assente | L'evento è solo un tag: niente cliente, scadenza, stato della consegna, note |
| Statistiche e riepiloghi | Assente | Resa della cernita, foto consegnate, tempo impiegato: utili per preventivi e per migliorare |
| Prestazioni su volumi grandi | Da misurare | Le prove automatiche usano immagini disegnate; serve un banco con 2.000 RAW reali |
| Affidabilità del motore | Da verificare | Il motore è un processo separato: va gestito il suo riavvio se si chiude durante una coda lunga |
| Multi-postazione | Parziale | Catalogo su rete con protezioni; un solo utente alla volta |
| Piattaforme | Solo Windows | Avalonia e il motore Python permetterebbero macOS in futuro |

## 4. Piano di modifiche

Ordinato per ondate: ognuna rende il flusso più completo o più veloce anche da sola. Sforzo: **S** giorni, **M** una
o due settimane, **L** più settimane. Le sigle dei file indicano dove si interviene.

### Ondata 1 — Chiudere i buchi del flusso

| # | Modifica | Cosa cambia per il fotografo | Dove | Sforzo |
|---|---|---|---|---|
| M1 | **Ritaglio, raddrizzamento e rotazione** nello Sviluppo, con proporzioni fisse (3:2, 4:5, 1:1, 16:9), griglia dei terzi, livella automatica sull'orizzonte; applicati in esportazione e scritti come `crs:Crop*` nel DNG | Niente più passaggi in un altro programma per le foto storte | `engine/src/denoiser` (impostazioni e render), `DevelopViewer`, `DevelopViewModel` | L |
| M2 | **Modello di metadati IPTC** (autore, copyright, contatti, condizioni d'uso) scelto all'import e modificabile per evento; scritto negli XMP e copiato nelle esportazioni | Consegne firmate, tutela del diritto d'autore | Import, `ExifToolMetadataWriter`, `XmpPublisher`, motore (`copy_metadata`) | M |
| M3 | **Selezione multipla ovunque** (Ctrl/Maiusc+clic, Ctrl A) in Consultazione, Revisione e striscia dello Sviluppo, con azioni in blocco: stelle, etichette, tag, persone, sviluppa, esporta | Un'azione su 300 foto invece di 300 azioni | `BrowseViewModel`, `ReviewViewModel`, `DevelopViewModel`, tabelle dei comandi | M |
| M4 | **Etichette colore** da tastiera (6–9 come Lightroom), filtri ed esportazione negli XMP | Il secondo livello di scelta che i fotografi usano già («rosso = album», «verde = cliente») | `ReviewViewModel`, `BrowseViewModel`, `XmpPublisher` (oggi forza il rosso su «da rivedere»: va ripensato) | S |
| M5 | **Più schede nello stesso evento** e **allineamento degli orari** tra corpi macchina (scatto di riferimento o differenza in minuti), con nomi che non collidono | I due corpi diventano un'unica sequenza in ordine di tempo | `ImportWizardViewModel`, pipeline di import, catalogo (offset per fotocamera) | M |
| M6 | **«Scheda al sicuro»** a fine import: quando ogni file ha almeno due copie verificate, la Home lo dice e offre di espellere la scheda | Si sa quando si può riformattare | Import, Home, `FileSystem` | S |

### Ondata 2 — Velocità con migliaia di foto

| # | Modifica | Cosa cambia per il fotografo | Dove | Sforzo |
|---|---|---|---|---|
| M7 | **Sviluppo per gruppi**: raggruppare la selezione per scena e luce simile (embedding già calcolati, esposizione e bilanciamento), «Sincronizza con il gruppo», preset di partenza applicato a tutto l'evento | Si sviluppano 20 gruppi invece di 400 foto | `DevelopViewModel`, motore (`cmd_sync_settings`), catalogo | M |
| M8 | **Precalcolo in background** della riduzione del rumore sulla selezione, con stima del tempo totale e **pausa dei job GPU** mentre si guarda l'anteprima | Niente attese all'esportazione, anteprime sempre pronte | Motore (coda lunga), `ExportQueue`, `JobManager` | M |
| M9 | **Cernita a schermo intero** con anteprime precaricate (successive e precedenti), zoom al 100% sul punto di messa a fuoco o sugli occhi, confronto con «candidata» fissa | Cernita più rapida su 2.000 foto | `ReviewViewModel`, `LargePhotoView` | M |
| M10 | **Riepilogo dell'evento** in Home e in Revisione: da decidere, scelte, sviluppate, consegnate, archiviate, con il passo successivo (decisione S4 non ancora completa) | Si vede a colpo d'occhio cosa manca | `HomeViewModel`, catalogo (`Exports`, `DevelopSettings`) | S |
| M11 | **Ricollegare i file dell'editor esterno**: il TIFF/PSD salvato da Photoshop accanto all'originale entra nel catalogo come versione della stessa foto | Il ritocco non si perde nell'archivio | `ExternalXmpSync`, catalogo (relazione originale–derivato) | M |

### Ondata 3 — Ricerca completa

| # | Modifica | Cosa cambia per il fotografo | Dove | Sforzo |
|---|---|---|---|---|
| M12 | **Filtri tecnici**: obiettivo (già nel catalogo), focale, diaframma, tempo, ISO, formato, libreria e cartella, etichetta, stato di lavorazione (sviluppata, esportata, mai consegnata) | «Le foto a 85 mm a f/1.8 consegnate nel 2025» | `SearchQuery`, `ISearchIndex`, `BrowseViewModel` | M |
| M13 | **Luoghi**: lettura del GPS, geotag da traccia GPX o dal telefono, nomi dei luoghi con un archivio offline (GeoNames), ricerca per luogo | «Tutte le foto fatte a Monza» anche senza tag | Metadati, catalogo, ricerca; nessun servizio in rete | L |
| M14 | **Raccolte**: album manuali e ricerche salvate che si aggiornano da sole (decisione D6 rimandata); raccolta «consegna al cliente» | Portfolio, selezioni ricorrenti, ristampe | Catalogo, Consultazione, Esporta (come ambito) | M |
| M15 | **Testo nelle foto** ricercabile (l'OCR esiste già per i numeri di gara) e ricerca esatta nelle didascalie | Cartelli, striscioni, pettorali | Analisi, ricerca | S |

### Ondata 4 — Archivio che dura anni

| # | Modifica | Cosa cambia per il fotografo | Dove | Sforzo |
|---|---|---|---|---|
| M16 | **Sposta evento in archivio**: copia verificata dal disco di lavoro al NAS o al disco d'archivio, catalogo aggiornato, poi liberazione dello spazio solo dopo conferma esplicita con l'elenco | Il disco veloce resta libero, l'archivio è ordinato | Librerie, `JobManager` (nuovo `IJobRunner`), catalogo; rispetta I4 | M |
| M17 | **Controllo dell'integrità** periodico (ricalcolo xxHash3 sugli originali e confronto con il catalogo) e **stato delle copie** per evento (1, 2 o 3 copie, segnale 3-2-1) | Ci si accorge di un file corrotto finché esiste un'altra copia | Job di manutenzione, Home, Librerie | M |
| M18 | **Anteprime per gli archivi scollegati**: miniature e anteprime grandi degli archivi conservate fuori dalla pulizia della cache | Si cerca e si guarda anche con il disco d'archivio in un cassetto | `MaintenanceService` (`TrimThumbnails`), Impostazioni | S |
| M19 | **Derivati collegati**: le esportazioni registrate come file figli dell'originale (`xmpMM:DerivedFrom`, già nel backlog), visibili nella scheda della foto, con «Riesporta con la stessa ricetta» | Si sa sempre cosa è stato consegnato e dove | Motore (export), catalogo (`Exports`), Consultazione | S |
| M20 | **Svuotare gli scarti** di un evento su richiesta, dopo una scadenza scelta dall'utente, con elenco e conferma (mai automatico) | Spazio recuperato senza rischi | Revisione, Librerie; rispetta I4 | S |

### Ondata 5 — Consegna e gestione del lavoro

| # | Modifica | Cosa cambia per il fotografo | Dove | Sforzo |
|---|---|---|---|---|
| M21 | **Ricette più complete**: filigrana (testo o logo, posizione, opacità), spazio colore (sRGB, Adobe RGB, Display P3), nitidezza di uscita per schermo o stampa, limite di peso, ZIP | Le consegne escono pronte senza passaggi esterni | Motore (`batch.py`, `settings.py`), `ExportRecipes` | M |
| M22 | **Provini per il cliente**: foglio provini PDF e galleria HTML statica da inviare; reimport delle scelte del cliente da elenco di nomi | Il ciclo con il cliente si chiude nel programma | Esporta, Revisione | M |
| M23 | **Scheda dell'evento**: cliente, luogo, data, note, stato e scadenza della consegna; la Home ordina il lavoro per scadenza | Il programma diventa anche l'agenda dei lavori | Catalogo, Home, Job | M |
| M24 | **Statistiche**: resa della cernita, foto consegnate per evento, tempi di lavorazione | Dati per preventivi e per migliorare il metodo | Home o pagina dedicata | S |

### Trasversali

- **Banco di prova con 2.000 RAW reali** (non nel repository) che misuri copia, analisi, cernita, sviluppo ed
  esportazione a ogni versione: oggi i test automatici usano immagini disegnate.
- **Riavvio automatico del motore** se si chiude durante una coda, con ripresa delle esportazioni non finite.
- **Correzioni dell'obiettivo** (distorsione, vignettatura, aberrazione cromatica) con profili lensfun: valutare la
  licenza (LGPL) come per LibRaw.
- **Regolazioni locali** (pennello, sfumato) solo dopo M1–M8: sono il lavoro più grande e oggi Photoshop/Lightroom
  restano a portata di Ctrl E.

## 5. Ordine consigliato

1. **M1, M3, M4, M2** — senza ritaglio, selezioni multiple, etichette e copyright il programma non regge un
   servizio vero da solo.
2. **M7, M8, M10** — rendono lo Sviluppo praticabile su centinaia di foto.
3. **M5, M6, M12, M14** — scarico con più corpi e ricerca/raccolte complete.
4. **M16–M19** — archivio di lungo periodo.
5. **M21–M24**, poi M13 e le regolazioni locali.
