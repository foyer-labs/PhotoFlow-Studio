# Domande frequenti · PhotoFlow 0.1.0

Le risposte rapide ai dubbi più comuni. Se non trovi la tua domanda, [apri una segnalazione su GitHub](https://github.com/foyer-labs/PhotoFlow/issues): rispondiamo lì.

> PhotoFlow 0.1.0 è la prima versione pubblica: funziona, ma è giovane. Le tue foto, però, sono al sicuro fin dal primo giorno: le regole qui sotto non si possono disattivare.

## Indice

1. [Sicurezza delle mie foto](#sicurezza-delle-mie-foto)
2. [Privacy](#privacy)
3. [Installazione e requisiti](#installazione-e-requisiti)
4. [Catalogo](#catalogo)
5. [Lightroom, Bridge e Photoshop](#lightroom-bridge-e-photoshop)
6. [Ricerca e filtri](#ricerca-e-filtri)
7. [Persone e volti](#persone-e-volti)
8. [Tag e didascalie](#tag-e-didascalie)
9. [Problemi e assistenza](#problemi-e-assistenza)

---

## Sicurezza delle mie foto

### PhotoFlow può cancellare le mie foto?

No. PhotoFlow non elimina mai un tuo file, né da solo né per errore di un'analisi. Al massimo **sposta** scarti e duplicati in una cartella, e solo quando glielo chiedi tu.

### Tocca i miei file RAW? E i JPEG?

I RAW non vengono **mai** modificati: stelle, parole chiave e persone vanno in un file `.xmp` accanto (il "sidecar"). Per le librerie create da un import, nei JPEG, TIFF e PNG i metadati vengono scritti dentro il file, così li vedono anche Esplora risorse e Photoshop; per gli archivi esistenti che aggiungi in Librerie, di default si scrive solo il file `.xmp` accanto e gli originali restano intatti.

### Legge i NEF «High Efficiency» delle Nikon Z (per esempio Z5 II, Z8)?

Sì. Per analizzarli e mostrarli PhotoFlow usa il JPEG che la fotocamera salva dentro il NEF, a piena risoluzione (sulla Z5 II 6048×4032). I dati RAW compressi in High Efficiency non vengono decodificati, e il file non viene mai modificato. Altri programmi (il visualizzatore di Windows, Photoshop/Camera Raw) li aprono solo se le loro versioni supportano già questo formato.

### Scrive qualcosa sulla scheda SD?

No: la scheda o la cartella da cui importi viene solo letta, mai modificata né svuotata. Ogni foto copiata viene riletta e confrontata con l'originale (controllo xxHash3), e nessuna analisi parte su un file la cui copia non è stata verificata. Se l'import si interrompe, riprende da dove era arrivato.

### Cosa succede agli scarti e ai duplicati?

In Revisione gli scarti vengono solo segnalati (in modalità prudente diventano "da rivedere"); con «Sposta scarti…» li sposti tu nella cartella `_scarti`, dentro la stessa libreria. Nella sezione Duplicati scegli quale copia tenere (clic o tasti 1-4) e le altre finiscono in `_duplicati`. In entrambi i casi niente viene cancellato: se cambi idea, i file sono lì.

### Le analisi automatiche possono sovrascrivere le mie scelte?

No. Stelle, esiti, tag, nomi delle persone, numeri di gara e didascalie che hai deciso tu non vengono mai sovrascritti da un'analisi, nemmeno quando cambi modello e rifai tutto. Un tag automatico che hai tolto resta tolto. E se sbagli, Ctrl Z annulla.

---

## Privacy

### Le mie foto finiscono nel cloud? Serve un account?

No e no. Tutti i modelli di intelligenza artificiale girano sul tuo PC: niente cloud, niente account, niente telemetria. Le foto non lasciano mai il computer.

### Quando usa internet? Funziona offline?

La rete serve solo per scaricare i modelli (e, se hai una scheda NVIDIA, il pacchetto GPU) e per controllare se c'è una nuova versione; il controllo degli aggiornamenti si spegne in **Impostazioni › Generali › Aggiornamenti**. Dopo la configurazione iniziale PhotoFlow funziona completamente offline. Se il PC non ha internet, puoi installare i modelli da una cartella copiata da un altro PC (vedi [Problemi e assistenza](#problemi-e-assistenza)).

### Cosa succede ai dati dei volti?

Le "impronte" dei volti, cioè i dati che servono a riconoscere una persona, sono **cifrate** sul PC e legate al tuo account Windows. Se elimini una persona, vengono cancellate tutte le sue impronte. E se i volti non ti servono, nella procedura d'import puoi mettere **Volti** su «Spento».

---

## Installazione e requisiti

### Funziona su Mac o Linux?

Per ora no: la 0.1.0 è solo per **Windows 10 e 11 a 64 bit**. Mac e Linux non sono ancora disponibili.

### Windows dice «Windows ha protetto il PC». È pericoloso?

Compare perché PhotoFlow non è ancora firmato digitalmente. Se hai scaricato il programma dalla [pagina Releases ufficiale](https://github.com/foyer-labs/PhotoFlow/releases/latest), clicca **«Ulteriori informazioni» › «Esegui comunque»**.

### Che PC mi serve?

Windows 10/11 a 64 bit con almeno 8 GB di RAM (meglio 16 GB). Su disco servono circa 1 GB per il programma, più i modelli (da circa 0,4 a 3,5 GB secondo le funzioni che usi) e le miniature (circa 2 GB ogni 60.000 foto). Per catalogo e miniature è consigliato un SSD.

### Mi serve una scheda video?

No: senza GPU funziona tutto sul processore, solo più lentamente. Con una scheda **NVIDIA** l'analisi è molto più veloce: il pacchetto GPU si scarica al primo avvio, dopo che hai accettato tu le licenze NVIDIA. In questa versione le schede di altre marche non vengono usate per accelerare: PhotoFlow lavora sul processore.

### Come si aggiorna?

PhotoFlow controlla all'avvio se c'è una nuova versione e te lo segnala nella barra di stato. Installi tu, da **Impostazioni › Generali › Aggiornamenti › «Scarica, installa e riavvia»**, mai mentre un lavoro è in corso. Catalogo, modelli e impostazioni restano come sono, e prima di aggiornare il catalogo viene fatto un backup.

### Se lo disinstallo, cosa resta sul PC?

Si disinstalla da **«App installate»** di Windows. Vengono tolti il programma, le miniature e la cache; **restano** il catalogo, i modelli, le impostazioni, i backup e i log, in `%LOCALAPPDATA%\PhotoFlow` (o nella cartella dove hai messo il catalogo). Le tue foto, ovviamente, non vengono toccate.

---

## Catalogo

### Dove si trova il catalogo?

Lo scegli tu al primo avvio, prima che venga creato: PhotoFlow propone una cartella locale, ma puoi indicarne un'altra. La posizione attuale è sempre visibile in **Impostazioni › Catalogo**.

![Impostazioni › Catalogo: posizione, backup, cifratura delle impronte, esportazione](img/12-impostazioni-catalogo.png)

### Posso spostarlo in un'altra cartella?

Sì: **Impostazioni › Catalogo › «Sposta…»**. La copia avviene al prossimo avvio, a catalogo chiuso, con un controllo d'integrità, e l'originale resta dov'era. Con **«Apri un catalogo esistente…»** puoi invece passare a un catalogo che hai già.

### Posso tenere il catalogo su una cartella di rete o un NAS?

Sì. Il catalogo può essere aperto da **un PC alla volta**: se è già aperto altrove, PhotoFlow te lo dice chiaramente. Miniature, cache e backup restano comunque sul PC locale.

### Il catalogo ha un backup?

Sì, automatico: a ogni chiusura e almeno una volta al giorno, con le ultime 10 versioni conservate; puoi farne uno subito con **«Backup ora»**. Per tornare indietro usa **Impostazioni › Catalogo › «Ripristina…»**: il ripristino si applica al prossimo avvio e il catalogo sostituito viene conservato accanto ai backup.

### Come porto il catalogo su un altro PC?

Da **Impostazioni › Catalogo › «Esporta catalogo…»** crei un unico file `.pfcatalog`; sull'altro PC lo apri con **«Importa e rimappa percorsi…»** e indichi dove si trovano ora le cartelle delle librerie. Per portare anche le impronte dei volti serve una password di esportazione; senza, nomi e assegnazioni viaggiano lo stesso e le impronte si ricalcolano.

---

## Lightroom, Bridge e Photoshop

### Lightroom, Bridge e Photoshop vedono il lavoro fatto in PhotoFlow?

Sì: stelle, etichette, parole chiave, persone (con le regioni dei volti) e, se vuoi, le didascalie vengono scritti in **XMP standard**, che questi programmi leggono. Per i RAW finiscono nel sidecar `.xmp`; le coppie RAW+JPEG ricevono gli stessi dati. Dopo un import, gli XMP si scrivono quando confermi la revisione (Ctrl Invio).

### Dove finiscono le parole chiave?

Nelle parole chiave normali (`dc:subject`) va il tag in inglese e, quando l'hai rivisto nel Vocabolario, anche in italiano; nelle parole chiave gerarchiche (`lr:hierarchicalSubject`) va `Categoria|Etichetta`, per esempio `Natura|Tramonto`, e le persone come `Persone|Nome`. Le parole chiave messe da te o da altri programmi non vengono mai toccate.

### Se cambio qualcosa in Bridge o Lightroom, PhotoFlow se ne accorge?

Sì. All'avvio e prima di ogni scrittura PhotoFlow rilegge gli XMP cambiati da altri programmi (oppure subito, da **Impostazioni › Generali › XMP › «Sincronizza ora»**): una parola chiave aggiunta diventa un tuo tag, una tolta spegne il tag automatico corrispondente, la descrizione diventa la tua didascalia, e su stelle ed etichette vince la modifica più recente. Dallo stesso punto puoi anche importare le persone che avevi già nominato in altri programmi.

---

## Ricerca e filtri

### Come combino più tag (AND/OR)?

Apri il pannello **Filtri** con il tasto F. I tag si scelgono a gruppi: dentro un gruppo valgono in OR, tra un gruppo e l'altro in AND, per esempio (auto OR moto) AND pista. L'interruttore AND/OR decide cosa fa il clic, Ctrl+clic fa l'altra cosa, e i numeri accanto a ogni tag ti dicono quante foto otterrai.

![Filtri combinati: una persona e un tag restringono il risultato a una foto](img/04-filtri.png)

### Come trovo le foto in cui ci sono due persone insieme?

Seleziona entrambe nella sezione Persone del pannello Filtri: le persone valgono sempre in AND, quindi vedi solo le foto in cui compaiono tutte. Gli eventi, invece, sono alternativi tra loro (OR).

### Posso cercare scrivendo una frase?

Sì. Nel campo di ricerca (Ctrl K) puoi scrivere nomi di persone, tag, eventi, nomi di file e cartelle, oppure una frase come «bambini che ridono al tramonto»: le foto vengono ordinate per significato. Con una foto selezionata, il tasto S trova quelle simili.

---

## Persone e volti

### Come do un nome alle persone?

PhotoFlow raggruppa da solo i volti simili; nella sezione **Persone** dai il nome a un gruppo (F2) e da lì in poi la persona viene riconosciuta nelle altre foto. Puoi anche unire due gruppi (M), separarli (S) o ignorarli (I).

### Un volto non è stato trovato. Posso aggiungerlo?

Sì. Apri l'anteprima grande in Consultazione (Spazio o doppio clic), premi **N**, trascina un riquadro sul volto e scegli la persona dall'elenco (o «+ Nuova persona…»). Il volto aggiunto a mano finisce negli XMP come gli altri; non aiuta però a riconoscere la persona in altre foto.

![Anteprima grande con i riquadri dei volti e i nomi; «?» indica un volto non riconosciuto](img/05-volti.png)

### Un volto ha il nome sbagliato. Come lo correggo?

Nell'anteprima grande fai clic sul riquadro del volto (un «?» o un nome sbagliato) e scegli la persona giusta. Dalla pagina di una persona puoi usare anche **Canc** («non è questa persona») o **Ctrl M** («Sposta a…»). La tua correzione è definitiva: nessuna analisi la cambierà. Solo tu puoi toglierla, con «Riparti da zero con le persone».

### Che differenza c'è tra buffalo_l e YuNet/SFace?

Sono due modelli per i volti, da scegliere in **Impostazioni › Modelli**. **buffalo_l** è consentito solo per uso **non commerciale** e si scarica solo dopo che hai accettato la sua licenza; **YuNet + SFace** hanno licenze permissive (MIT e Apache 2.0) e vanno bene anche per lavoro, ma non rilevano gli occhi chiusi. Se cambi modello con «Usa questo modello», PhotoFlow rifà l'analisi dei volti e i nomi che hai dato restano.

### Posso usare un modello migliore per riconoscere le persone?

Sì: in **Impostazioni › Modelli** c'è **AdaFace IR-101**, opzionale. Nelle prove sbaglia circa la metà delle volte rispetto ad ArcFace sui volti piccoli o sfocati; sui volti normali i due modelli si equivalgono. È solo per uso non commerciale e si converte sul tuo PC: accetta la licenza, premi «Scarica i file originali» e poi «Verifica e converti». Poi sceglilo in **«Riconoscimento delle persone»** e premi «Usa e rigenera i volti». Il rilevamento dei volti e degli occhi resta quello di prima.

### Voglio ricominciare da zero con le persone. Come faccio?

Nella rigenerazione dei volti spunta **«Riparti da zero con le persone»**. PhotoFlow ti dice quante persone, assegnazioni fatte a mano e volti disegnati eliminerà, in tutto il catalogo, e ti chiede di confermare. L'eliminazione avviene quando applichi la rigenerazione completa. Prima viene fatto un backup del catalogo, così puoi tornare indietro da «Ripristina…». I volti vengono poi raggruppati di nuovo e puoi ridare i nomi.

---

## Tag e didascalie

### I tag sono in italiano o in inglese?

In tutte e due. Le etichette italiane sono già incluse, ma finché non le rivedi nel **Vocabolario** negli XMP finisce solo l'inglese. Nel Vocabolario puoi correggere le traduzioni, mettere in lista nera i tag inutili e creare tag personali descritti a parole.

### Le didascalie sono in italiano?

Le didascalie automatiche sono in inglese (modello Florence-2, facoltativo, si sceglie nell'import). In italiano servono una scheda **NVIDIA con almeno 12 GB** e una conversione da fare una volta sola in **Impostazioni › Modelli** («Verifica e converti»: circa 15 minuti, con circa 45 GB liberi durante la conversione). La didascalia che scrivi tu (tasto D in Consultazione) non viene mai sostituita.

### Le didascalie finiscono negli XMP?

Solo se lo vuoi: attiva **Impostazioni › Generali › XMP › «Scrivi le didascalie in dc:description»** (è spento per default). In ogni caso le didascalie sono cercabili in PhotoFlow.

---

## Problemi e assistenza

### Senza scheda video è lentissimo. È normale?

Sul solo processore alcune analisi richiedono circa un secondo o più per foto, quindi un grande archivio richiede tempo: è previsto. Al primo avvio un piccolo benchmark stima i tempi e, senza GPU, propone un livello di tag più leggero. Puoi continuare a lavorare mentre le analisi girano in background.

### Un modello non si scarica. Cosa faccio?

Controlla la connessione e riprova con **«Scarica»** in **Impostazioni › Modelli**; per buffalo_l devi prima spuntare «Accetto la licenza di questo modello». Ogni file viene verificato (SHA-256) prima dell'installazione, quindi un download incompleto non viene usato. In alternativa, scarica i modelli su un altro PC, copia la cartella e usa **«Importa pacchetto offline…»**.

### Dove trovo i log?

Nella cartella `%LOCALAPPDATA%\PhotoFlow\logs` (incollala nella barra degli indirizzi di Esplora risorse). Sono file locali: non vengono inviati a nessuno. Se PhotoFlow si chiude all'improvviso, al riavvio ti propone di riprendere i lavori interrotti.

### Come segnalo un problema o chiedo una funzione?

Apri una segnalazione su [GitHub](https://github.com/foyer-labs/PhotoFlow/issues): descrivi cosa stavi facendo, cosa ti aspettavi e cosa è successo, e allega le righe utili dei log. Per favore **non allegare foto personali**: se serve un'immagine, usane una che puoi condividere liberamente.
