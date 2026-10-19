# PhotoFlow Studio 0.2.0

La versione del servizio da 2.000 foto: le ondate 1 e 2 del piano nato dalla valutazione «da fotografo»
([docs/studio/valutazione.md](https://github.com/foyer-labs/PhotoFlow-Studio/blob/main/docs/studio/valutazione.md)).
Il programma ora regge da solo un servizio intero, dalla scheda alla consegna, ed è più veloce quando le
foto sono centinaia.

## Novità

**Scarico e messa in sicurezza**
- **Due corpi macchina, un evento.** L'import elenca le fotocamere della scheda: si corregge l'orario di quella
  indietro e gli scatti si alternano nell'ordine vero. La seconda scheda dello stesso evento continua la
  numerazione. I doppioni si riconoscono comunque.
- **Scheda al sicuro.** La Home dice quando ogni foto della scheda è nel catalogo e verificata in almeno due
  posti: si può formattarla. «Espelli» la toglie.
- **Autore e copyright** in Impostazioni › Generali, modificabili per evento all'import: negli XMP e in ogni
  foto consegnata («© {anno} Nome Cognome»).

**Cernita**
- **Selezione multipla** (Ctrl+clic, Ctrl A) in Revisione, Consultazione e Sviluppo: P, X, stelle, etichette e
  tag su tutte insieme, con un solo Ctrl Z.
- **Etichette colore** 6–9 come in Lightroom, con i filtri; l'etichetta scelta vince sul rosso automatico.
- **Foto grande più veloce**: le successive sono già pronte; **Z** mostra al 100% gli occhi o il punto di fuoco.
- **Candidata** nel confronto (K): resta ferma mentre la raffica le passa accanto. **F11** schermo intero.

**Sviluppo**
- **Inquadratura**: ritaglio con proporzioni (C), raddrizzamento anche automatico, rotazione e specchio; nelle
  consegne e, come istruzioni standard, nei DNG per Lightroom.
- **Gruppi di luce simile** nella striscia: G seleziona il gruppo, Ctrl Maiusc G lo sincronizza.
  «Punto di partenza per le non sviluppate» dà a tutte il look della foto aperta.
- **Preparazione in background** della riduzione del rumore: le foto vicine si aprono subito, «Prepara tutte»
  con il tempo che manca. Mentre si sviluppa, le analisi dei job lasciano la precedenza all'anteprima.

**Consegna e archivio**
- Le foto esportate portano **autore, copyright, parole chiave, persone, didascalia e stelle**.
- Il tempo che manca nelle esportazioni in coda.
- **Riepilogo degli eventi** in Home: da decidere, scelte, sviluppate, consegnate, e il passo successivo.
- **Versioni ritoccate**: i file salvati da Photoshop accanto all'originale compaiono nel pannello della foto.
- Filtro per **etichetta** e **modifiche in blocco** (stelle, etichette, tag) in Consultazione.

## Correzioni

- L'istogramma dello Sviluppo compariva solo dopo la prima modifica.
- Le scorciatoie della Home e del pannello «Mostra in Esplora risorse» erano quelle vecchie.

## Installazione

Scarica **`PhotoFlowStudio-0.2.0-Setup.exe`**. Chi ha già la 0.1.0 riceve l'aggiornamento dalle Impostazioni.
Il catalogo si aggiorna da solo (migrazione 20, con copia di sicurezza prima). Il programma non è firmato: se
Windows mostra «Windows ha protetto il PC», scegli «Ulteriori informazioni» › «Esegui comunque».

## Limiti noti

- Il raddrizzamento non va nel DNG (rotazione e ritaglio sì): va rifatto in Lightroom.
- Autore e copyright si scrivono negli XMP alla conferma della Revisione; le foto già confermate li ricevono
  alla prossima modifica o con «Sincronizza XMP».
- Le prove automatiche usano immagini disegnate: i tempi su 2.000 RAW reali sono da misurare.
