# PhotoFlow Studio — progetto

PhotoFlow Studio unisce **PhotoFlow** (import verificato, culling assistito, persone, tag, ricerca,
duplicati, XMP) e **Denoiser** (sviluppo RAW, riduzione del rumore, esportazione) in un solo programma
che copre tutto il ciclo di una foto: **importa → scegli → sviluppa → consegna**, più l'archivio.

Le decisioni qui sotto sono la sintesi delle consulenze di sei specialisti (interfaccia visiva, esperienza
d'uso, psicologia cognitiva, captologia, progettazione dell'interazione, workflow fotografico), chiesti
di stabilire il miglior ordine delle attività. Dove le opinioni divergevano è indicata la scelta fatta e
il perché. Le decisioni di PhotoFlow (`docs/spec.md`, D1–D24, P1–P22) restano valide salvo dove una
decisione S le estende.

## Architettura

**S1 — Due programmi, un motore.** La base è PhotoFlow (.NET 10, Avalonia 12). Il codice Python di
Denoiser vive in `engine/` ed è avviato come processo separato, `photoflow-engine.exe` (PyInstaller),
che riceve richieste JSON su stdin e risponde su stdout (`engine/src/denoiser/server.py`). È la stessa
via già approvata per le didascalie (U4, `PhotoFlow.Vlm`): l'eccezione di CLAUDE.md §3 è estesa a questo
motore. Riscrivere 9.000 righe di calcolo numerico in C#, o 46.000 righe di PhotoFlow in Python, non
avrebbe portato nulla all'utente.

- Code separate nel motore: anteprime e regolazioni non aspettano mai un'esportazione.
- Annullamento di ogni richiesta lunga; avanzamento come eventi.
- Le anteprime passano come PNG nella cartella temporanea dell'app, cancellate da `InternalFileCleaner`.

## Ordine delle attività e navigazione

**S2 — La barra laterale segue il flusso.** *(UI, UX, captologia; psicologia: «una domanda per schermata»)*

| Gruppo | Sezioni (Ctrl+cifra nell'ordine visibile) |
|---|---|
| Lavoro | Home 1 · Job 2 · Revisione 3 · **Sviluppo 4** · **Esporta 5** |
| Archivio | Persone 6 · Consultazione 7 · Duplicati 8 |
| In basso | Librerie 9 · Vocabolario · Profili · Impostazioni (Ctrl ,) |

*Divergenza:* l'esperto di interazione proponeva di lasciare i numeri di PhotoFlow e dare a Sviluppo
Ctrl 8 per la memoria muscolare; prevale l'idea che i numeri coincidano con l'ordine visibile, perché
PhotoFlow Studio è un programma nuovo e la barra di stato insegna «Ctrl 1–9 Sezioni».

**S3 — Scegliere prima di sviluppare.** *(tutti)* Il culling viene prima dello sviluppo: la riduzione
del rumore neurale costa secondi o minuti a foto e non va spesa sugli scarti; giudicare (rapido,
intuitivo) e regolare (analitico) sono compiti diversi e non vanno mescolati.

**S4 — Un tunnel che non imprigiona.** *(captologia, UX)* A ogni passaggio il programma propone il
passo successivo, una sola volta e senza finestre: fine Revisione → «Sviluppa le migliori (N)»,
«Esporta le migliori»; la Home mostra l'evento in corso con i contatori di cosa resta (da decidere,
scelte, sviluppate, esportate). Le sezioni restano tutte raggiungibili liberamente.

**S5 — Una selezione di lavoro condivisa.** *(UX, interazione, psicologia)* Le foto mandate allo
Sviluppo (dalla Revisione: le migliori e tenute del job; dalla Consultazione: i risultati) formano la
**selezione di lavoro**, che segue l'utente tra Sviluppo ed Esporta e dice sempre da dove viene
(«Da Revisione · Gara Monza · 92 foto»). La foto aperta è distinta dalla selezione.

## Sviluppo

**S6 — Pagina a tre fasce.** *(UI)* Barra strumenti in alto; la foto al centro su fondo neutro, con
prima/dopo a tendina; striscia di miniature in basso (stesso aspetto della griglia di Revisione);
pannello a destra.

**S7 — Pannello in blocchi.** *(psicologia: chunking e divulgazione progressiva; UI: l'istogramma è il
riferimento costante)* Dall'alto: istogramma con curva (sempre visibile) · foto (ISO, rumore misurato) ·
punto di partenza · **Luce** (esposizione, alte luci, ombre, luminosità) · **Colore** (bilanciamento con
temperatura, tinta, contagocce; saturazione) · **Toni** (contrasto) · **Rumore** · **Avanzate**
(chiuse: dettaglio, pixel caldi, quando ridurre, ricostruzione dei colori).

**S8 — Riduzione del rumore non modale.** *(UX, interazione, psicologia; l'UI chiedeva tre stati
visibili)* Niente più «Applica»: la riduzione è una regolazione come le altre, **attiva di serie**, con un
**preset automatico** scelto dal rumore misurato (basso → Delicato, medio → Bilanciato, alto → Forte) e
un metodo classico veloce; il metodo neurale è una scelta attiva. Si spegne con **R**. Lo stato è
sempre visibile: «attiva · metodo · −54%», «in calcolo» con barra sottile, «spenta». Al 100% si vede
subito la zona visibile; con la foto intera prima arriva lo sviluppo, poi il risultato completo.

**S9 — Regolazioni nel catalogo.** *(UX, workflow)* Il catalogo è la fonte di verità anche per lo
sviluppo (tabella `DevelopSettings`), con la data di esportazione (`Exports`). I file `.denoiser` di
Denoiser vengono letti come punto di partenza. Lo sviluppo **non** va mai negli XMP dei RAW (namespace
`crs:`): Lightroom lo scambierebbe per il proprio. Nei DNG esportati sì, come istruzioni.

**S10 — Applicare a più foto è esplicito.** *(interazione, psicologia)* «Applica alle selezionate (N)»
copia dalla foto aperta **solo i campi cambiati**, mostra il numero nel pulsante e si annulla. Copia e
incolla delle regolazioni. Niente sincronizzazione automatica.

## Esportazione

**S11 — Una pagina con la coda, non una finestra.** *(UI, interazione, psicologia)* Esporta è una
sezione: cosa esportare (selezione di lavoro, migliori dell'evento, tutto l'evento), una **ricetta**,
destinazione, nome, cosa fare se il file esiste (scelto prima, «altro nome» di serie). Le esportazioni
girano in coda mentre si continua a lavorare; il risultato dice esportate, saltate, errori, con «Apri
cartella». Ogni foto esce con le sue regolazioni (fotografate quando entra in coda).

**S12 — Ricette di consegna.** *(captologia, workflow)*

| Ricetta | Uscita |
|---|---|
| Web e social | JPEG, lato lungo 2048 px, qualità 85, senza GPS, sottocartella `web` |
| Cliente | JPEG a piena risoluzione, qualità 92, sottocartella `cliente` |
| Stampa | TIFF 16 bit, sottocartella `stampa` |
| Lightroom (DNG ripulito) | DNG a mosaico accanto all'originale, `{nome}-PF-NR`, toni come istruzioni Camera Raw (si possono escludere: «solo pulizia») |
| Personalizzata | tutte le opzioni |

Nuovi segnaposti per i nomi: `{evento}`, `{stelle}`, `{seq}`.

**S13 — Scorciatoie.** *(interazione; una sola tabella)*

| Tasto | Significato in tutto il programma |
|---|---|
| Ctrl E | Apri nell'editor esterno (come in PhotoFlow) |
| Ctrl Maiusc E | Esporta la selezione (apre Esporta) |
| Ctrl Maiusc R | Mostra in Esplora risorse (era Ctrl Maiusc E in PhotoFlow) |
| Spazio | «Più da vicino»: foto grande in Revisione/Consultazione, adatta ↔ 100% in Sviluppo |
| \ (tenuto) | Mostra l'originale |
| ← → · Pag↑ Pag↓ | Foto precedente / successiva |

In Sviluppo: **R** riduzione del rumore sì/no, **W** contagocce, **Y** tipo di confronto,
**Ctrl Maiusc C / V** copia / incolla regolazioni, **Ctrl Maiusc S** applica alle selezionate,
**Ctrl Z / Ctrl Y** annulla / ripeti (per foto). In Esporta: **Ctrl Invio** avvia.

## Principi trasversali

- **IA che spiega:** ogni proposta automatica dice il motivo (rumore misurato e ISO per il preset,
  segnali per gli esiti); le decisioni manuali non vengono mai sovrascritte. *(psicologia, captologia)*
- **Annullare invece di confermare**, conferme solo per ciò che è irreversibile; mai sovrascrivere né
  cancellare di serie. *(psicologia)*
- **Niente dark pattern**, niente notifiche non provocate dall'utente, niente punteggi. *(captologia)*
- **Un solo stile:** token, font e componenti di PhotoFlow; nessun colore d'esito usato per altro.
  *(UI)*

## Versione 0.2.0: il servizio da 2.000 foto

Decisioni prese con la valutazione «da fotografo» ([`valutazione.md`](valutazione.md), ondate 1 e 2, M1–M11).

**S14 — Inquadratura nello Sviluppo (M1).** Rotazione di 90°, specchio, raddrizzamento (anche automatico
sulle linee lunghe) e ritaglio con proporzioni, applicati dopo lo sviluppo dal motore
(`engine/src/denoiser/geometry.py`). Le anteprime, le zone al 100% e il contagocce lavorano nelle coordinate
dell'uscita. Nei DNG la rotazione va nell'orientamento e il ritaglio nei tag standard DefaultCrop; il
raddrizzamento no (nota nell'esito dell'esportazione). Tasto C; Invio o doppio clic conferma.

**S15 — Autore e diritti (M2).** In Impostazioni › Generali; ogni import può cambiare autore e copyright per
il suo evento (salvati nel job). Scritti come IPTC in XMP alla conferma della Revisione e, con parole chiave,
persone, didascalia e stelle, in ogni foto esportata. «{anno}» diventa l'anno dello scatto.

**S16 — Selezione multipla ed etichette colore (M3, M4).** Ctrl+clic e Ctrl A in Revisione, Consultazione e
striscia dello Sviluppo; P, X, stelle ed etichette valgono per tutta la selezione con un solo Ctrl Z. Tasti
6–9 come Lightroom (rosso, giallo, verde, blu); l'etichetta dell'utente vince sul rosso automatico «da
rivedere». Filtri per etichetta in Revisione e in Consultazione.

**S17 — Due corpi, più schede (M5).** All'import ogni fotocamera della scheda ha la sua correzione
dell'orario: gli scatti si ordinano con l'ora corretta, ma i doppioni si riconoscono con quella della
fotocamera (colonna `ClockOffsetMs`, migrazione 20). Una seconda scheda dello stesso evento continua la
numerazione.

**S18 — Scheda al sicuro (M6).** La Home dice quando ogni foto della scheda è nel catalogo e verificata in
almeno due posti (o perché no) e offre «Espelli». La scheda non viene mai toccata (I1).

**S19 — Sviluppo per gruppi (M7).** Le foto della selezione si raggruppano per pause di oltre 15 minuti o
cambi di esposizione di oltre 1 EV; G seleziona il gruppo, Ctrl Maiusc G lo sincronizza. «Punto di partenza
per le non sviluppate» copia luce, colore, toni e rumore (non l'inquadratura).

**S20 — Precalcolo (M8).** Le foto vicine a quella aperta si preparano da sole in una coda del motore
separata; «Prepara tutte» con il tempo che manca; le anteprime restano in cache sotto le loro regolazioni
esatte. Mentre si sviluppa, le analisi dei job non iniziano nuove foto per qualche secondo. Le esportazioni
in coda mostrano il tempo che manca.

**S21 — Cernita veloce (M9).** Le foto grandi successive si decodificano in anticipo; Z sulla foto grande
mostra al 100% gli occhi del volto più grande, o il punto di fuoco, o il centro; K nel confronto fissa la
candidata e → porta accanto lo scatto successivo; F11 schermo intero.

**S22 — Riepilogo degli eventi (M10).** La Home elenca gli ultimi eventi con da decidere, scelte, sviluppate
e consegnate, e il pulsante del passo successivo.

**S23 — Ritorno dall'editor esterno (M11).** I file salvati accanto all'originale con lo stesso nome (PSD,
TIFF, JPEG…) compaiono come «Versioni ritoccate» nel pannello della foto, con Apri e Mostra. Nessun
servizio in ascolto: si guarda la cartella quando la foto si apre.

## Rimandato (backlog)

Raggruppamento per scene simili in Sviluppo, cronologia persistente delle regolazioni, profilo di flusso
«pre-processore per Lightroom» scelto all'import, filigrana e nitidezza di uscita, `xmpMM:DerivedFrom`,
pausa dei job GPU durante l'anteprima, foglio provini del riepilogo dell'evento.
