# Novità

Tutte le modifiche rilevanti, dalla più recente. Formato ispirato a
[Keep a Changelog](https://keepachangelog.com/it/1.1.0/); numerazione [SemVer](https://semver.org/lang/it/).

## [Non rilasciato]

## [0.14.0] - 2026-10-18

### Modificato

- Le scelte del salvataggio (cartella, formato, bit, qualità JPEG, nome del file, metadati) non sono più
  nel pannello a destra, che ora regola soltanto la foto: si scelgono nella finestra che si apre con
  **Esporta…** (`Ctrl+E`), la stessa di *Esporta selezionate*. La finestra ripropone le scelte
  dell'ultima esportazione.
- *Esporta come…* non c'è più: *Esporta* apre sempre la finestra.
- Nella finestra di esportazione una nota spiega come le regolazioni arrivano nel DNG.

## [0.13.0] - 2026-10-18

### Aggiunto

- Pannello **Sviluppo** per RAW, JPG, PNG e TIFF: **curva dei toni** modificabile con il mouse sopra
  l'**istogramma** RGB (aggiornato a ogni regolazione, con avviso di ombre e alte luci tagliate);
  bilanciamento del bianco con **Temperatura**, **Tinta** e **Contagocce**; **Esposizione**,
  **Contrasto**, **Luminosità**, **Alte luci** (recupero delle zone quasi bruciate dei RAW), **Ombre** e
  **Saturazione**.
- **Regolazioni di ogni foto**: sviluppo e riduzione del rumore vengono ricordati per ciascuna foto, in
  un file accanto (`DSC_0001.NEF.denoiser`); nel carosello le foto regolate hanno un punto giallo.
  Con **Applica le modifiche a tutte le selezionate** una regolazione cambiata sulla foto aperta vale
  anche per le altre selezionate (raffiche). Annulla e ripeti valgono per ogni foto.
- **Esporta selezionate** usa le regolazioni di ciascuna foto; il metodo si può ancora imporre a tutte.
- Nei **DNG** il bilanciamento scelto diventa l'AsShotNeutral del file e i toni (contrasto, alte luci,
  ombre, saturazione, curva) impostazioni di Lightroom/Camera Raw.
- Riga di comando: `--temperatura` e `--tinta` per il bilanciamento. Le regolazioni salvate per ogni foto
  valgono nell'interfaccia; la riga di comando usa quelle indicate o un preset.

### Modificato

- Il bilanciamento del bianco non risviluppa più il RAW: è una correzione esatta nei colori della
  fotocamera, istantanea. Il RAW viene risviluppato solo cambiando la ricostruzione dei colori.
- La sezione «Sviluppo RAW» diventa «Sviluppo» (per tutte le foto) e «RAW» (ricostruzione dei colori,
  DNG Converter); i valori R / G / B del bilanciamento lasciano il posto a temperatura e tinta.
- Esportando un DNG, ExifTool non copia più dall'originale bilanciamento, esposizione di base e
  impostazioni di Camera Raw, che vengono dalle regolazioni.

## [0.12.0] - 2026-10-18

### Aggiunto

- **Apri cartella…** (`Ctrl+Maiusc+O`, oppure trascinando una cartella nella finestra): le foto della
  cartella compaiono in un **carosello** di miniature sotto la foto. Un clic apre una foto, `Pag↑` e
  `Pag↓` passano alla precedente o alla successiva; `Ctrl+clic` e `Maiusc+clic` ne selezionano più d'una.
- **Esporta selezionate…** (`Ctrl+B`): esporta tutte le foto selezionate nel carosello, scegliendo
  metodo, cartella, formato e cosa fare se il file esiste già. Scegliendo il DNG, per le foto JPG, PNG
  e TIFF (che non possono diventare DNG) si sceglie a parte il formato.
- **Applica** (`Ctrl+Invio`): la foto si apre così com'è e la riduzione del rumore parte solo quando
  la applichi, dopo aver scelto il metodo. Da quel momento l'anteprima segue le regolazioni; ogni
  nuova foto riparte dall'originale.

### Modificato

- **Esporta come…** apre una finestra con cartella (stessa della foto, sottocartella accanto alla
  foto o una cartella a scelta) e formato (TIFF, PNG, JPEG o DNG). La scelta vale poi anche per
  **Esporta**, che salva subito.
- La foto esportata tiene il **nome dell'originale** (`DSC_0001.NEF` → `DSC_0001.tif`), senza «_dn».
  Se il file coincide con l'originale (stessa cartella e stesso formato) si aggiunge `_1`: l'originale
  non viene mai sovrascritto. La riga di comando resta com'era (`{nome}_dn`).
- *Elabora più file* è sostituita dal carosello con *Esporta selezionate*.
- Annulla, Ripeti e Guida nella barra mostrano solo icona e tasto, per fare spazio a *Esporta come…*.
- Le scorciatoie mostrano i nomi italiani dei tasti (`Maiusc`, `Invio`, `Pag↑`).

## [0.11.2] - 2026-10-14

### Corretto

- I NEF Nikon «alta efficienza» (HE/HE*, per esempio della Z 5II) davano un errore generico: LibRaw
  riconosce il file ma non sa decodificarne i dati, e l'errore emergeva in un punto che saltava la
  conversione. Ora, con Adobe DNG Converter installato, vengono convertiti e aperti da soli (anche
  al livello 3, sui dati del sensore). Senza il convertitore compare un messaggio che spiega il
  motivo, con il link per scaricarlo.
- Adobe DNG Converter produce DNG compatibili con LibRaw (`-dng1.4`), anche nelle versioni recenti.
- I link nei messaggi d'errore si possono cliccare.

## [0.11.1] - 2026-10-11

### Corretto

- Dopo aver scelto Restormer (con il suo preset o seguendo il consiglio nella descrizione), *Quando
  ridurre* restava su «Dopo lo sviluppo» anche tornando a DRUNet o agli altri metodi: con i RAW
  DRUNet sembrava non avere quasi effetto. Restormer ora resta «Prima dello sviluppo», dove sulle foto
  vere rende bene, e ogni preset predefinito riporta i RAW su «Prima dello sviluppo».

## [0.11.0] - 2026-10-11

### Aggiunto

- **RawNIND (IA)**: rete di Brummer e De Vleeschouwer (UCLouvain, 2025) addestrata su coppie vere
  di RAW rumorosi e puliti di reflex e mirrorless. Lavora direttamente sui dati del sensore,
  conserva molto dettaglio e un aspetto naturale ed è veloce anche senza scheda video. Il risultato
  torna sui fotositi, quindi sviluppo, bilanciamento del bianco e DNG dei dati del sensore restano
  quelli dell'app. Dopo lo sviluppo, per i sensori non Bayer e per i JPG, usa la sua versione su
  RGB lineare. Modello ONNX scaricato al primo uso dalle release di darktable (55 MB, pesi CC BY 4.0).
- **Restormer (IA)**: rete *transformer* di Zamir e altri (CVPR 2022) addestrata su rumore reale.
  Dà un risultato molto pulito ma è lenta e conviene con una scheda video; con i RAW rende meglio
  «Dopo lo sviluppo». Pesi ufficiali (100 MB, MIT) convertiti in ONNX sul computer, senza PyTorch.
- Preset «Neurale RAW naturale (RawNIND)» e «Neurale molto pulito (Restormer)».
- Confronto delle reti con il rumore misurato su una Nikon D5100 nella pagina «Come funziona».

### Corretto

- Le regolazioni avanzate del metodo precedente non restano più visibili per un istante quando si
  cambia metodo.

## [0.10.1] - 2026-10-11

### Corretto

- All'avvio di `Denoiser.exe` non compare più una piccola finestra grigia vuota prima della schermata
  di avvio: ora c'è solo la schermata con la barra di avanzamento.

## [0.10.0] - 2026-10-11

### Aggiunto

- **Risultato a qualsiasi ingrandimento**: con la foto rimpicciolita (*Adatta*, 50%, 25%…) l'app
  elabora la foto intera in background, con percentuale e tempo rimanente. Si vede così l'effetto
  sull'immagine nel suo insieme e non solo su un dettaglio, cosa utile soprattutto con i sensori da
  molti megapixel. Una volta pronto, il risultato vale per ogni ingrandimento e spostamento senza
  altri calcoli. Da vicino resta il calcolo rapido della sola zona visibile.

### Migliorato

- Anteprima più leggera in memoria: resa tonale a strisce e immagini della vista a 8 bit.

## [0.9.3] - 2026-10-11

### Aggiunto

- **Schermata di avvio**: appena si avvia `Denoiser.exe` compare subito una schermata con il nome
  dell'app, poi una barra di avanzamento con percentuale e fase in corso (calcolo numerico,
  algoritmi, reti neurali, scheda video, interfaccia) fino all'apertura della finestra. Non resta
  più il dubbio se il doppio clic abbia funzionato.

## [0.9.2] - 2026-10-11

### Corretto

- L'app poteva chiudersi all'improvviso, senza messaggi, passando più volte tra DRUNet e SCUNet
  con la scheda video (DirectML): ora sulla scheda video resta caricata una sola rete alla volta, e
  DRUNet usa tasselli di misura fissa, così la memoria della scheda non si esaurisce.
- Se l'app dovesse comunque chiudersi per un errore della scheda video o di una libreria, il punto
  in cui è successo viene annotato in `%LOCALAPPDATA%\Denoiser\errori.log`.

## [0.9.1] - 2026-10-11

### Corretto

- Il pannello delle impostazioni poteva risultare tagliato a destra (caselle numeriche e menu
  nascosti), per esempio su Windows con la scala dello schermo al 125–150% o spostando la finestra
  su un altro monitor: ora la colonna si adegua sempre al contenuto, e se serve compare una barra di
  scorrimento orizzontale.
- Nomi di file lunghi nel pannello «Foto» e percorsi lunghi nei file recenti non allargano più la finestra.
- Nome della fotocamera senza la marca ripetuta (es. «NIKON D5100» invece di
  «NIKON CORPORATION NIKON D5100»).
- Nessun messaggio d'errore chiudendo l'app con un'anteprima in corso.

## [0.9.0] - 2026-10-11

Prima versione pubblicata: applicazione completa, in attesa del collaudo sui RAW reali.
Codice distribuito con licenza MIT.

### Elaborazione

- Riduzione del rumore dei **RAW sui dati del sensore** (livello 3, sensori Bayer) e dopo lo sviluppo
  (livello 2, tutti i sensori); JPG, PNG e TIFF a 8/16 bit.
- **Misura del rumore sulla foto stessa** (modello Poisson-gaussiano), senza profili per fotocamera;
  «gemello di rumore» per misurarlo dopo la demosaicizzazione.
- Metodi **Wavelet**, **Non-Local Means**, **Bilaterale** e reti neurali **DRUNet** e **SCUNet**
  eseguite in locale con ONNX Runtime (DirectML sulla scheda video, ripiego sul processore).
  I modelli si scaricano al primo uso dalle pubblicazioni ufficiali e vengono verificati.
- Forze separate per grana e macchie di colore, recupero del dettaglio, rimozione dei pixel caldi
  che conserva stelle e luci puntiformi.
- Bilanciamento del bianco (come scattato, automatico, luce diurna, personalizzato), sette algoritmi
  di demosaicizzazione, luminosità automatica ed esposizione.
- Lettura dei RAW non supportati da LibRaw tramite Adobe DNG Converter.

### Salvataggio

- TIFF e PNG a 8/16 bit, JPEG, **DNG** (dati del sensore ripuliti oppure DNG lineare).
- Copia completa dei metadati con ExifTool (incluso), profilo colore conservato, impostazioni
  registrate nel file, scrittura atomica, originale mai sovrascritto.
- Elaborazione di più foto e cartelle, preset predefiniti e personali, riga di comando completa.

### Interfaccia

- Interfaccia in italiano a tema scuro, con pagina iniziale e file recenti.
- Confronto prima/dopo a tendina al 100%, viste «solo dopo», «solo prima» e «rumore tolto».
  Tieni premuto Spazio per vedere l'originale.
- Scorciatoie sempre visibili sui pulsanti, guida rapida (F1), annulla/ripeti, spiegazione di ogni
  controllo nel pannello.
- Anteprima corretta sui monitor ad alta densità (125–200%) e nella vista ridotta.
- Operazioni lunghe interrompibili con avanzamento e tempo stimato.

### Qualità e robustezza (revisione del codice)

- Niente aloni colorati sui bordi neutri con bilanciamenti del bianco reali.
- Stima del rumore del sensore molto più accurata nelle ombre.
- Anteprima di una zona coincidente con la foto esportata.
- Correzioni di memoria, concorrenza, cache e gestione degli errori; registro degli errori in
  `%LOCALAPPDATA%\Denoiser\errori.log`.

[Non rilasciato]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.14.0...HEAD
[0.14.0]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.12.0...v0.13.0
[0.12.0]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.11.2...v0.12.0
[0.11.2]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.11.1...v0.11.2
[0.11.1]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.11.0...v0.11.1
[0.11.0]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.10.1...v0.11.0
[0.10.1]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.9.3...v0.10.0
[0.9.3]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.9.2...v0.9.3
[0.9.2]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.9.1...v0.9.2
[0.9.1]: https://github.com/foyer-labs/Image-Denoiser/compare/v0.9.0...v0.9.1
[0.9.0]: https://github.com/foyer-labs/Image-Denoiser/releases/tag/v0.9.0
