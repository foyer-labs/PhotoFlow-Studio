# PhotoFlow Studio

### Dalla scheda SD alla consegna: importa, scegli, sviluppa, esporta. Senza che le foto lascino mai il tuo PC.

PhotoFlow Studio unisce **PhotoFlow** (import verificato, scelta assistita delle migliori, tag in italiano, persone, ricerca, duplicati) e **Denoiser** (sviluppo RAW, riduzione del rumore classica e neurale, esportazione) in un solo programma che copre tutto il ciclo di una foto. L'intelligenza artificiale gira **sul tuo computer**: niente cloud, niente account.

[![Versione 0.1.0](https://img.shields.io/badge/versione-0.1.0-informational)](https://github.com/foyer-labs/PhotoFlow-Studio/releases/latest)
[![Windows 10 | 11](https://img.shields.io/badge/piattaforma-Windows%2010%20%7C%2011%20(64%20bit)-0078D6)](#-scarica-e-installa)
[![Licenza Apache 2.0](https://img.shields.io/badge/licenza-Apache%202.0-blue)](LICENSE)

**[⬇️ Scarica PhotoFlow Studio 0.1.0](https://github.com/foyer-labs/PhotoFlow-Studio/releases/latest)** · [Il flusso di lavoro](docs/studio/flusso.md) · [Guida utente](docs/guida/README.md) · [Domande frequenti](docs/guida/faq.md) · [Per sviluppatori](docs/SVILUPPO.md)

<p align="center">
  <img src="docs/studio/img/s03-sviluppo.png" alt="Lo Sviluppo di PhotoFlow Studio: la foto al centro con il prima/dopo a tendina, la striscia della selezione in basso, a destra istogramma, curva e regolazioni di luce, colore e rumore" width="100%">
</p>

```
 Home → Import → Job → Revisione → Sviluppo → Esporta        Archivio: Persone · Consultazione · Duplicati
 Ctrl 1   Ctrl I   Ctrl 2   Ctrl 3      Ctrl 4     Ctrl 5                 Ctrl 6     Ctrl 7          Ctrl 8
```

- 🔒 **Tutto sul tuo PC.** Le foto non escono mai dal computer. Nessun cloud, nessun account, nessuna telemetria.
- 🛡️ **Non tocca mai i tuoi originali.** La scheda SD resta com'era, i RAW non vengono mai modificati, nessuna foto viene cancellata. Lo sviluppo vive nel catalogo e finisce solo nei file esportati.
- 🎯 **Prima scegli, poi sviluppi.** La riduzione del rumore non si spreca sugli scarti: dalla Revisione un clic manda le migliori allo Sviluppo, e da lì all'Esportazione.
- 🎚️ **Sviluppo e rumore insieme.** Esposizione, bilanciamento del bianco, contrasto, curva, saturazione, alte luci e ombre, con la riduzione del rumore già attiva su un preset scelto dal rumore misurato.
- 📦 **Consegne in un gesto.** Ricette pronte (web, cliente, stampa, DNG ripulito per Lightroom) e una coda che lavora mentre tu continui.
- 🔎 **Trovi qualsiasi foto in un attimo.** Per persona, evento, tag, data o descrivendola a parole: *"bambini che ridono al tramonto"*.

Pensato per chi scatta tanto (eventi, viaggi, motorsport), ha archivi di decine di migliaia di foto, RAW e JPEG, e vuole un solo programma dalla scheda alla consegna, senza rinunciare a Lightroom quando serve.

---

## ✨ Cosa fa

### Importa senza paura

Inserisci la scheda, scegli un nome per l'evento e via. PhotoFlow copia le foto, **ricontrolla ogni file dopo la copia**, le rinomina con il modello che preferisci (anno, data, evento…) e può salvarle in più destinazioni insieme, per avere subito un backup. Riconosce le foto già importate e, se qualcosa si interrompe, riprende da dove era rimasto. La scheda non viene mai modificata né cancellata.

<img src="docs/guida/img/13-import.png" alt="Import da scheda SD, passo 1: sorgente, foto nuove da importare, nome e data dell'evento, anteprima del percorso" width="100%">

### Scegli le migliori in pochi minuti

Nella **Revisione** PhotoFlow ha già fatto il primo giro: segnala le foto mosse o sfocate, gli occhi chiusi e i problemi di esposizione, raggruppa le raffiche e ti propone la migliore. Rosso per gli scarti, giallo per quelle da rivedere, verde per la migliore. L'ultima parola è tua, tutta da tastiera: **P** tieni, **X** scarta, **1-5** stelle, **C** confronta.

<img src="docs/guida/img/02-revisione.png" alt="Revisione: griglia di foto con gli esiti colorati (scarto, da rivedere, migliore), le stelle e i filtri per esito" width="100%">

Quando hai scelto, **«Sviluppa le migliori (N)»** porta le migliori e le tenute allo Sviluppo.

### Sviluppa, con il rumore già sotto controllo

La foto al centro con il **prima/dopo a tendina**, la striscia delle foto scelte in basso, il pannello a destra nell'ordine in cui si ragiona: istogramma e curva, luce, colore, toni, rumore. La riduzione del rumore è **attiva di serie** con un preset scelto dal rumore misurato (e ti dice perché), si spegne con **R**; il metodo neurale è una scelta tua. Le regolazioni si salvano da sole, con annulla per foto, e si estendono alle altre foto solo quando premi **«Applica alle selezionate»**.

<img src="docs/studio/img/s04-sviluppo-modifiche.png" alt="Sviluppo con regolazioni di esposizione, saturazione e contrasto e alcune foto selezionate nella striscia per applicare le stesse regolazioni" width="100%">

### Esporta con una ricetta

**Web e social**, **Cliente**, **Stampa**, **Lightroom (DNG ripulito)** o personalizzata: scegli cosa esportare (le foto in lavorazione, le migliori dell'evento, tutto l'evento), la ricetta e la cartella, e premi **Ctrl Invio**. L'esportazione va in coda e tu continui a lavorare; alla fine sai quante foto sono uscite e apri la cartella con un clic. Mai un file sovrascritto di nascosto.

<img src="docs/studio/img/s06-esporta.png" alt="La pagina Esporta: cosa esportare, le ricette di consegna, formato, cartella e nome dei file" width="100%">

### Trova qualsiasi foto

Scrivi cosa cerchi, anche con parole tue, oppure apri i **Filtri** (tasto **F**): persone, eventi e tag con il numero di foto accanto, da combinare come vuoi. Qui, *Giulia* + *spiaggia* = una foto. Hai trovato quella giusta? **Trova simili** (S) ti mostra le altre che le somigliano, e con **Ctrl E** la apri in Photoshop.

<img src="docs/guida/img/04-filtri.png" alt="Filtri combinati in Consultazione: la persona Giulia e il tag spiaggia restituiscono una sola foto" width="100%">

### Sa chi c'è nella foto

Nell'anteprima grande ogni volto ha il suo riquadro con il nome, o un **?** se la persona è ancora sconosciuta. Fai clic sul volto per dire chi è. Un volto non è stato trovato? Premi **N** e disegnalo tu.

<img src="docs/guida/img/05-volti.png" alt="Anteprima grande di una foto in spiaggia con i riquadri sui volti: Giulia, Sofia e un volto sconosciuto con il punto interrogativo" width="100%">

### Le tue persone, in ordine

PhotoFlow raggruppa i volti simili. Tu dai i nomi, unisci e separi i gruppi, e dalla pagina di ogni persona ritrovi tutte le sue foto. Le impronte dei volti restano sul PC, cifrate.

<img src="docs/guida/img/06-persone.png" alt="La sezione Persone con tre persone riconosciute e con nome: Marco, Sofia e Giulia" width="100%">

### Duplicati sotto controllo

Trova le copie esatte e le foto quasi uguali, te le mette una accanto all'altra e ti indica quale conviene tenere. Scegli tu: le altre vengono **spostate** nella cartella `_duplicati`, mai cancellate.

<img src="docs/guida/img/09-duplicati.png" alt="Duplicati: due copie della stessa foto affiancate, una da spostare e una da tenere, con la scelta consigliata" width="100%">

### E inoltre

- 🏷️ **Tag automatici in italiano e inglese**, divisi per categorie (Natura, Luoghi, Veicoli, Persone…), con un vocabolario tuo: rivedi le traduzioni, escludi i tag che non vuoi, aggiungi i tuoi.
- ✍️ **Didascalie automatiche** in inglese e, con una scheda NVIDIA da almeno 12 GB, anche in italiano. Quella scritta da te non viene mai sovrascritta.
- 🏁 **Numeri di gara**: per il motorsport legge i numeri su auto e moto e li collega all'elenco iscritti.
- 📚 **Archivi esistenti**: aggiungi una cartella e PhotoFlow la analizza in background, senza spostare niente.
- 🔄 **Cambi modello? Nessun problema**: PhotoFlow rifà le analisi, ti mostra cosa cambierebbe e applica solo se vuoi. Le tue decisioni restano.
- 💾 **Il catalogo dove vuoi**, anche su una cartella di rete, con backup automatici.
- ⌨️ **Tutto da tastiera**, interfaccia scura o chiara, in italiano e in inglese. La barra in basso ti ricorda sempre i tasti della schermata.

---

## ⬇️ Scarica e installa

1. Vai alla pagina **[Releases](https://github.com/foyer-labs/PhotoFlow-Studio/releases/latest)** e scarica **`PhotoFlowStudio-0.1.0-Setup.exe`**.
2. Avvialo.
3. Al primo avvio scegli dove salvare il catalogo. Poi PhotoFlow Studio ti propone i modelli di intelligenza artificiale da scaricare e, se hai una scheda NVIDIA, il pacchetto GPU. Ogni licenza ti viene mostrata prima: decidi tu.
4. Inserisci una scheda e premi **Ctrl I**, oppure aggiungi una cartella di foto in **Librerie**.

> **Windows dice «Windows ha protetto il PC»?** Niente paura. PhotoFlow Studio 0.1.0 non è ancora firmato digitalmente, e SmartScreen avvisa per tutti i programmi in questa situazione. Fai clic su **Ulteriori informazioni** e poi su **Esegui comunque**. Il codice di PhotoFlow Studio è tutto qui, pubblico, da leggere.

**Requisiti**

| | |
|---|---|
| Sistema | Windows 10 o 11, 64 bit |
| Memoria | 8 GB di RAM (consigliati 16 GB) |
| Disco | circa 1,5 GB (programma e motore di sviluppo), più i modelli (da ~0,4 a ~3,5 GB secondo le funzioni) e le miniature. Meglio un SSD |
| Scheda video | Non obbligatoria. Con una NVIDIA tutto è molto più veloce. Didascalie in italiano: NVIDIA con almeno 12 GB |

PhotoFlow Studio usa un catalogo e cartelle proprie (`%LOCALAPPDATA%\PhotoFlowStudio`): può convivere con PhotoFlow e con Denoiser già installati. I modelli neurali di riduzione del rumore si scaricano dallo Sviluppo, la prima volta che li scegli.

PhotoFlow Studio ti avvisa all'avvio quando esce una nuova versione (puoi spegnere il controllo in Impostazioni › Generali). L'aggiornamento lo installi tu, mai mentre sta lavorando.

---

## 🔒 Privacy prima di tutto

- **Tutto in locale.** Nessun cloud, nessun account, nessuna telemetria. La rete serve solo per scaricare i modelli e per controllare gli aggiornamenti, e quest'ultimo puoi spegnerlo.
- **Nessuna foto cancellata.** Scarti e duplicati si spostano in cartelle (`_scarti`, `_duplicati`) solo quando lo chiedi tu.
- **Originali intatti.** La scheda SD non viene mai toccata. I RAW nemmeno: accanto a ogni foto c'è solo un piccolo file `.xmp`.
- **Le tue decisioni vincono sempre.** Stelle, tag, persone e didascalie che hai messo tu non vengono mai sovrascritte dalle analisi automatiche.
- **Volti protetti.** Le impronte dei volti sono cifrate sul tuo PC.

---

## 📖 Guida e aiuto

- **[Il flusso di lavoro](docs/studio/flusso.md)**: da dove si parte e dove si arriva, con screenshot e schemi.
- **[Guida utente](docs/guida/README.md)**: import, revisione, persone, ricerca e duplicati, passo per passo, con le scorciatoie.
- **[Guida dello sviluppo e del rumore](engine/docs/guida.md)**: le regolazioni e i metodi di riduzione del rumore del motore (ex Denoiser).
- **[Domande frequenti](docs/guida/faq.md)**: le risposte rapide ai dubbi più comuni.
- Hai trovato un problema? Aprilo nelle [Issues](https://github.com/foyer-labs/PhotoFlow-Studio/issues).

---

## 🚧 Limiti della 0.1.0

È la prima versione pubblica: funziona, ma è giovane.

- Solo Windows. Mac e Linux non ci sono ancora.
- Non è firmata digitalmente, da qui l'avviso di SmartScreen.
- I modelli si scaricano a parte: la prima volta serve una connessione (oppure un pacchetto preparato su un altro PC).
- Il modello dei volti buffalo_l è solo per uso non commerciale. In alternativa puoi scegliere YuNet + SFace.
- Senza scheda video l'analisi è più lenta: per alcune funzioni circa un secondo o più per foto.
- Le didascalie in italiano richiedono una NVIDIA da 12 GB e una conversione una tantum (circa 15 minuti, con ~45 GB liberi durante la conversione).

---

## 📄 Licenza e crediti

PhotoFlow Studio è open source con licenza **Apache 2.0**: vedi [`LICENSE`](LICENSE) e [`NOTICE`](NOTICE). È un progetto di **Foyer Labs**, nato dall'unione di [PhotoFlow](https://github.com/foyer-labs/PhotoFlow) e [Image-Denoiser](https://github.com/foyer-labs/Image-Denoiser): la storia di entrambi è in questo repository, il motore di sviluppo in [`engine/`](engine/).

PhotoFlow si appoggia a librerie e modelli di altri, ognuno con la propria licenza. I modelli non sono inclusi nel programma: si scaricano al primo uso, dopo averti mostrato la licenza. L'elenco completo è in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Vuoi compilarlo, provarlo o contribuire? Parti da **[Per sviluppatori](docs/SVILUPPO.md)**.

<sub>Le persone negli screenshot (Giulia, Marco, Sofia) sono personaggi inventati e disegnati: nessuna foto reale.</sub>
