# Report — riconoscimento delle persone con AdaFace e azzeramento delle persone

Data: 15 ottobre 2026 · Richiesta dell'utente · Decisione P22 (spec §7, §17)

## Perché

L'utente ha chiesto un modello di riconoscimento dei volti migliore, a parità di dimensioni o poco più grande. Ha chiesto anche di poter rigenerare i volti delle foto già analizzate e di cancellare le persone inserite a mano. Alla domanda sull'ambito ha risposto «Tutto, persone comprese».

**AdaFace IR-101 WebFace12M** (CVLFace, 249 MB) usa lo stesso ritaglio e la stessa normalizzazione di ArcFace e dà 512 valori. È più preciso sui volti piccoli e sfocati, i casi frequenti nelle foto di eventi.

## Misure (laboratorio, LFW, 6.000 coppie)

| Condizione | Errori ArcFace w600k_r50 | Errori AdaFace IR-101 |
|---|---|---|
| Volti normali | 0,15% | 0,18% |
| Volti di 24 px | 0,47% | 0,28% |
| Volti di 16 px | 1,65% | **0,80%** |
| Sfocatura forte (raggio 4) | 1,23% | **0,72%** |

**Soglie.** Sono tarate su LFW: 13.215 volti, 3 milioni di coppie di persone diverse. Alla soglia di ArcFace (0,45) i falsi riconoscimenti sono 1,3 su un milione. AdaFace ottiene lo stesso tasso a 0,42. A quella soglia riconosce il 99,79% delle coppie della stessa persona, contro il 99,56% di ArcFace. Il suggerimento parte da 0,32, cioè la fascia di 0,10 già usata.

**Parità.** La conversione ONNX, l'allineamento e l'adattatore C# sono stati confrontati con la rete PyTorch originale degli autori, il `model.py` di CVLFace alla revisione fissata (`tools/lab/adaface_refs.py`). Su 200 volti il coseno minimo è 0,99999, sia su CPU sia su CUDA.

## Cosa è cambiato

| Area | Modifica |
|---|---|
| Manifest | `adaface-ir101-webface12m`, nuova funzione `FaceRecognition`. Pesi a revisione fissa `54f602a`, con SHA-256 verificati contro Hugging Face. Installazione `manual-convert`, con il nuovo campo `output` |
| Licenza | Il codice AdaFace è MIT. La scheda dei pesi rimanda alla licenza del set di addestramento (WebFace): il modello è trattato come **non commerciale** e richiede l'accettazione esplicita, come buffalo_l |
| Download | «Scarica i file originali» porta i file nella cartella sorgente del modello, solo dopo l'accettazione della licenza. Gli indirizzi sono fissati e i file verificati; quelli già presenti e integri restano |
| Conversione | `tools/vlm/recipes/adaface_ir101`, codice nostro. Usa l'ambiente Python della conversione del livello D, gira su CPU e non richiede la GPU. Il risultato viene verificato contro PyTorch |
| Scelta | Impostazioni › Modelli › «Riconoscimento delle persone»: ArcFace, SFace o AdaFace, scelti a parte dal rilevatore. «Usa e rigenera i volti» apre la rigenerazione su tutto il catalogo |
| Rigenerazione | Nuovo perimetro «Tutto il catalogo». Nuova opzione «Riparti da zero con le persone» |
| Persone | Soglie per modello anche nell'indicazione dei volti dubbi della vista Persone |

## Azzeramento delle persone e invarianti

- **Cosa elimina, in tutto il catalogo:**
  - le persone, anche quelle con nome;
  - le assegnazioni, anche quelle manuali, compreso «non è questa persona»;
  - i suggerimenti e i campioni;
  - i volti disegnati a mano.
- **Cosa resta:** i volti rilevati con le loro impronte. Dopo l'azzeramento vengono raggruppati di nuovo, per tutti gli anni.
- **I5.** L'azzeramento avviene solo come azione esplicita dell'utente, in tre passi:
  1. spuntare l'opzione;
  2. vedere i conteggi e confermare con «Sì, elimina tutte le persone»;
  3. avviare la rigenerazione completa, con i conteggi rifatti nell'anteprima, e poi «Applica».

  Non avviene mai con la prova su campione né con una rigenerazione senza l'opzione.
- **Backup.** Prima dell'applicazione PhotoFlow fa un backup del catalogo (`…-people-reset-…`), che si può ripristinare da Impostazioni › Catalogo.
- **XMP.** I file già scritti vengono riscritti: spariscono le keyword e le regioni con i nomi eliminati. Le regioni scritte da altri programmi restano.
- **I1/I2.** Nessuna scrittura sulle sorgenti né sui RAW: le regole di scrittura degli XMP non sono cambiate.

## Test

| Test | Cosa verifica |
|---|---|
| `Invariants/PeopleResetInvariantTests` | Senza l'opzione e con la prova su campione le persone restano. Con l'opzione spariscono in due librerie; i volti rilevati restano; gli XMP perdono Sara e Luca e tengono la regione di un altro programma (exiftool) |
| `App/RegenerationResetTests` | La pagina propone tutto il catalogo, mostra i conteggi e non parte senza conferma. L'anteprima riconta; «Applica» fa il backup ed elimina |
| `App/FaceRecognizerChoiceTests` | AdaFace è usato solo se convertito, scelto e con la licenza accettata; il rilevatore non cambia; ogni modello ha le sue soglie |
| `Inference/ModelRegistryTests` | Voce del manifest. Il download dei file originali avviene solo dopo l'accettazione; un file alterato viene rifiutato; il modello convertito si apre dal suo file |
| `Parity/FaceEmbeddingParityTests` (adaface, CPU e GPU) | Parità con la rete degli autori (locale) |

## Verifiche manuali per l'utente

1. In Impostazioni › Modelli, sulla riga di AdaFace: spuntare l'accettazione della licenza, premere «Scarica i file originali» (249 MB) e poi «Verifica e converti». Al termine deve comparire «Modello convertito e pronto».
2. In «Riconoscimento delle persone» scegliere AdaFace IR-101 e premere «Usa e rigenera i volti». Controllare che il perimetro sia «Tutto il catalogo».
3. Provare prima «Prova su campione» senza azzeramento e guardare l'anteprima: le assegnazioni devono passare ai nuovi volti.
4. Se si vuole ripartire da zero: spuntare «Riparti da zero con le persone», controllare i conteggi, confermare, fare «Rigenera tutto» e poi «Applica». Infine controllare la vista Persone e un XMP già scritto, per esempio aprendolo in Lightroom o Bridge.

Prima di queste prove non sono state fatte verifiche sul catalogo reale dell'utente: i file di AdaFace non sono stati scaricati nel profilo dell'utente perché la licenza va accettata dall'utente nell'app.
