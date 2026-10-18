# PhotoFlow Studio 0.1.0

La prima versione di **PhotoFlow Studio**: PhotoFlow e Denoiser in un solo programma, dalla scheda SD alla
consegna. **Importa → Scegli → Sviluppa → Consegna**, con l'archivio (persone, ricerca, duplicati) sempre a
portata di mano.

## Cosa c'è

- **Tutto PhotoFlow 0.1.0**: import verificato, Revisione con raffiche ed esiti spiegati, tag e didascalie in
  italiano, persone e volti, Consultazione con ricerca a parole, duplicati, XMP per Lightroom e Bridge.
- **Sviluppo** (Ctrl 4): la foto con il prima/dopo a tendina, la striscia delle foto scelte, istogramma e curva
  modificabile, esposizione, alte luci, ombre, luminosità, bilanciamento del bianco con contagocce, saturazione,
  contrasto. La **riduzione del rumore** di Denoiser è attiva di serie con un preset scelto dal rumore misurato
  (e ti dice perché), si spegne con R; metodi classici e neurali. Regolazioni salvate da sole nel catalogo,
  annulla e ripeti per foto, «Applica alle selezionate» che copia solo i campi cambiati.
- **Esporta** (Ctrl 5): cosa esportare (le foto in lavorazione, le migliori dell'evento, tutto l'evento), ricette
  pronte (Web e social, Cliente, Stampa, Lightroom DNG ripulito, Personalizzata), segnaposti `{evento}`, `{stelle}`,
  `{seq}`, una coda che lavora mentre continui. Mai un file sovrascritto di nascosto.
- **Un flusso che ti accompagna**: la barra laterale e Ctrl 1–9 seguono l'ordine del lavoro; alla fine della
  Revisione «Sviluppa le migliori (N)», dalla Consultazione «Sviluppa i risultati», Ctrl Maiusc E esporta da
  qualsiasi pagina.

Il flusso completo, con screenshot e schemi: [docs/studio/flusso.md](https://github.com/foyer-labs/PhotoFlow-Studio/blob/main/docs/studio/flusso.md).

## Correzioni rispetto a PhotoFlow 0.1.0

- La Revisione di un job con una copia identica di una foto in un'altra cartella restava senza titolo, senza
  raffiche e senza proposte: ora si apre normalmente.

## Installazione

Scarica **`PhotoFlowStudio-0.1.0-Setup.exe`** e avvialo. Il programma non è firmato: se Windows mostra «Windows
ha protetto il PC», scegli «Ulteriori informazioni» › «Esegui comunque».

PhotoFlow Studio usa cartelle proprie (`%LOCALAPPDATA%\PhotoFlowStudio`) e convive con PhotoFlow e Denoiser già
installati. Il catalogo, i modelli e il pacchetto GPU di PhotoFlow non vengono letti: al primo avvio scegli
dove tenere il nuovo catalogo e scarica i modelli che ti servono.

## Limiti

- Solo Windows 10/11 a 64 bit; installer non firmato.
- Pacchetto di circa 420 MB: contiene il motore di sviluppo (Python, LibRaw, OpenCV, ONNX Runtime con DirectML).
- I modelli neurali (analisi e riduzione del rumore) si scaricano a parte, al primo uso.
- Lo sviluppo vive nel catalogo e nei file esportati: non viene scritto negli XMP dei RAW, per non confondere
  Lightroom (nei DNG esportati sì, come istruzioni Camera Raw).
