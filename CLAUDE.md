# CLAUDE.md — PhotoFlow

Istruzioni permanenti per Claude Code su questo repository. Leggile all'inizio di ogni sessione.

## 1. Fonte di verità

| Documento | Ruolo |
|---|---|
| `docs/spec.md` | Specifica completa e vincolante. In caso di dubbio vince la specifica |
| `docs/spec.md` §2 | Registro delle decisioni D1-D24 |
| `docs/spec.md` §12bis | Sistema visivo, struttura finestra, componenti, mappa Avalonia, scorciatoie |
| `docs/spec.md` §13 | Fasi 0-5 e criteri di accettazione |
| `docs/spec.md` §17 | Decisioni di Design P1-P20 (vincolanti), backlog, verifiche tecniche |
| `docs/design/*.html` | Tavole di Design: riferimento visivo, non codice da riutilizzare |

**Controllo delle modifiche.** Non modificare mai una decisione in silenzio. Se un requisito è ambiguo, irrealizzabile o in conflitto con un altro, fermati e proponi la modifica: prima si aggiorna `docs/spec.md`, poi il codice. Le idee nuove vanno nel backlog (§17), non nel codice.

## 2. Invarianti (mai violabili, sempre coperte da test)

| # | Invariante |
|---|---|
| I1 | Nessuna scrittura né cancellazione sulla sorgente di import (scheda SD o cartella sorgente) |
| I2 | I file RAW non vengono mai modificati: solo sidecar `.xmp` |
| I3 | Nessuna fase successiva parte su un file la cui copia non è stata verificata (xxHash3) |
| I4 | Nessun file dell'utente viene eliminato automaticamente; scarti e duplicati si spostano solo su richiesta esplicita (i file interni di PhotoFlow nelle cartelle di `IAppPaths` seguono le regole di `docs/spec.md` §10, decisione B1) |
| I5 | Tag, decisioni e assegnazioni manuali non vengono mai sovrascritti da processi automatici |

Il test di un'invariante si scrive **prima** del codice che la riguarda.

## 3. Architettura e confini

- `PhotoFlow.Core` non ha dipendenze esterne: entità, interfacce, orchestrazione.
- Ogni libreria esterna (ONNX Runtime, exiftool, OpenCvSharp, SkiaSharp, Magick.NET, HdbscanSharp, RapidOcrNet, Florence2-Sharp, SQLite) sta dietro un'interfaccia di `Core`.
- Tutti i percorsi passano da `IAppPaths`. Nessun percorso scritto nel codice.
- Ricerca testuale e vettoriale solo tramite `ISearchIndex` (predisposizione PostgreSQL).
- Backend di inferenza solo tramite `IInferenceProvider` (CUDA / CoreML / CPU; nuovi backend solo dopo i test di parità).
  **Eccezione approvata dall'utente (13 ott 2026, decisione U4 in `docs/plans/fase-5b.md`):** le didascalie VLM (livello D, Qwen2.5-VL-7B) girano con ONNX Runtime GenAI in un processo separato (`PhotoFlow.Vlm`) dietro `IImageCaptioner`, con verifica approssimata contro il modello originale (spec §5) invece della parità esatta. Nessun altro modello può usare questa via senza una nuova decisione.
- Pipeline: fasi per foto collegate da `System.Threading.Channels` limitati; barriere di lotto come `IBatchStage`.
- Dependency injection con `Microsoft.Extensions.DependencyInjection`; log con `ILogger` (Serilog come sink).

Struttura prevista:

```
docs/            spec.md, design/
src/             PhotoFlow.Core, .Sources, .Imaging, .Metadata, .Inference,
                 .Analysis, .Faces, .Tagging, .Data, .App
tests/           PhotoFlow.Tests (+ eventuali progetti per categoria)
tools/lab/       Python: benchmark, export ONNX, riferimenti di parità (uv)
models/          manifest.json (MAI i file dei modelli)
profiles/        profili di scena predefiniti (JSON versionati)
```

## 4. Convenzioni di codice

- .NET LTS corrente, C# con `nullable` abilitato e avvisi trattati come errori.
- Codice, identificatori e commenti in **inglese**; testi dell'interfaccia solo in file di risorse IT/EN.
- Ogni operazione di I/O o inferenza è asincrona e accetta un `CancellationToken`.
- Nessuno stato statico mutabile.
- Intestazione SPDX in ogni file sorgente: `// SPDX-License-Identifier: Apache-2.0`.
- Migrazioni del catalogo: script SQL numerati (`NNNN_descrizione.sql`), solo in avanti, backup automatico prima dell'applicazione.
- Ogni risultato automatico salva la versione del modello che l'ha prodotto.

## 5. Interfaccia (Avalonia)

- Colori, tipografia, spaziature e raggi **solo** dai token di §12bis, definiti in un unico `ResourceDictionary` con varianti Dark/Light. Nessun colore scritto direttamente in una vista.
- Font IBM Plex Sans/Mono incorporati come risorse; cifre tabulari dove i numeri cambiano.
- Animazioni solo ≤ 120 ms su opacità e colore; niente shimmer, niente sfocature, niente Acrylic/Mica.
- Scorciatoie: un'unica tabella dei comandi per vista, da cui si generano sia i `KeyBindings` sia la barra di stato.
- Nessuna operazione bloccante sul thread dell'interfaccia.
- Se una soluzione delle tavole non è realizzabile fedelmente in Avalonia, **segnalalo** e proponi un'alternativa: non approssimare in silenzio.

## 6. Modelli

- I file dei modelli non entrano mai nel repository (`.gitignore`).
- `models/manifest.json` contiene per ogni modello: funzione, nome, versione, URL, SHA-256, dimensione, licenza, adattatore.
- I modelli non commerciali (buffalo_l) richiedono accettazione esplicita della licenza prima del download.
- Ogni modello ha un adattatore di input/output e un test di parità contro i riferimenti generati in `tools/lab/`.

## 7. Test

Categorie (trait xUnit `Category`):

| Categoria | Dove gira | Dati |
|---|---|---|
| `Unit` | Ovunque, CI | Nessun file |
| `Parity` | Locale | Riferimenti in cartella esterna |
| `Integration` | Ovunque, CI | File sintetici o con licenza libera |
| `Scenario` | Ovunque, CI | Cartelle simulate |
| `Quality` | Solo locale | Foto personali etichettate |
| `Performance` | Solo locale | Subset d'archivio |

- I dati locali si trovano nel percorso indicato dalla variabile d'ambiente `PHOTOFLOW_TESTDATA`. Se non è impostata, i test locali vengono **saltati**, non falliscono.
- Comandi tipici:
  - `dotnet build`
  - `dotnet test --filter Category=Unit`
  - `dotnet test --filter "Category=Unit|Category=Integration|Category=Scenario"`

## 8. Privacy (il repository è pubblico)

- Nessuna foto personale, nessun volto reale, nessun dato del catalogo nel repository.
- I dati di test personali vivono **fuori** dalla cartella del repository.
- Un hook pre-commit blocca immagini e file RAW fuori da `tests/data/public/` (che contiene solo file con licenza libera verificata e documentata).
- Log e report non devono contenere impronte dei volti né chiavi.

## 9. Modo di lavorare

1. Lavora **una fase alla volta**, nell'ordine di §13. Non anticipare funzioni di fasi successive.
2. All'inizio di una fase: piano dettagliato (progetti toccati, interfacce, test da scrivere, rischi) e **attendi approvazione**.
3. Durante la fase: commit piccoli e descrittivi, un branch per fase (`phase/N-nome`).
4. A fine fase: report con la checklist dei criteri di accettazione di §13, ciascuno con l'evidenza (test, misura, verifica manuale da fare). **Fermati** e attendi approvazione prima della fase successiva.
5. Le verifiche manuali (es. Esplora risorse, Photoshop) vanno elencate come istruzioni per l'utente, non dichiarate superate.
6. Se un'informazione esterna è incerta (licenza, versione, stato di una libreria), verificala e riporta la fonte; se non puoi verificarla, dichiaralo.

## 10. Da non fare mai

- Cancellare o modificare file nella sorgente, o RAW.
- Committare modelli, foto personali, chiavi o dati del catalogo.
- Introdurre YOLO Ultralytics o altre dipendenze con licenza copyleft forte (AGPL/GPL) nel codice distribuito.
- Cambiare decisioni della specifica senza proposta esplicita.
- Scrivere percorsi assoluti nel codice: tutto passa da `IAppPaths`.
- Marcare un criterio di accettazione come superato senza evidenza.
