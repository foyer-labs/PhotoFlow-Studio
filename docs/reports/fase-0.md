# Report di fine fase 0 — Setup e laboratorio

Data: 2026-10-12 · Branch: `phase/0-setup` · PR: foyer-labs/PhotoFlow#1 · Piano: [`docs/plans/fase-0.md`](../plans/fase-0.md)

## Checklist dei criteri di accettazione

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Soluzione compilabile con tutti i progetti e le interfacce di `Core` | ✅ Superato | `dotnet build` 0 avvisi / 0 errori (avvisi = errori); CI verde su `windows-latest` e `ubuntu-latest`; `ArchitectureTests` (confini dei progetti, Core senza dipendenze) |
| 2 | Benchmark Python eseguibile su una cartella di foto, con report dei tempi per fase | ✅ Superato | `tools/lab/benchmark.py` su 230 foto reali (178 NEF, 52 JPG): report in `PHOTOFLOW_TESTDATA/lab/benchmark/20261012-190042/` (tabella sotto) |
| 3 | D-FINE esportato in ONNX con test di parità superato | ✅ Superato | `export_dfine.py` (S e M, opset 17). `DFineParityTests` su 40 foto reali, per S e M, nostro export e onnx-community: **rilevamenti** 359/359 (S) e 421/421 (M) abbinati, IoU min 0,9998, Δ punteggio max 1,5e-4; **tensori** (query abbinate, decisione T1) Δ punteggi max 2,7e-3 ≤ 5e-3, Δ riquadri max 1,5e-4 ≤ 5e-4. Pre-elaborazione C# identica bit per bit a quella ufficiale |
| 4 | Hook pre-commit dimostrato con un tentativo di commit di un'immagine | ✅ Superato | Tentativo reale nel repository: `DSC_0001.jpg` (estensione) e `appunti.txt` (firma JPEG) bloccati, exit code 1. `PreCommitHookTests` (8 casi, CI Windows + Linux); job CI `hygiene` su tutti i file tracciati |

## Cosa è stato fatto

| Area | Contenuto |
|---|---|
| Repository | `.gitignore`, `.gitattributes`, `LICENSE` (Apache-2.0 canonica), `NOTICE`, `README`, `global.json` (SDK 10.0.401), gestione centralizzata dei pacchetti, SPDX imposto in build (IDE0073) |
| Core | Interfacce di §3 (+ `IInferenceSession`, `IObjectDetector`, `IImageOperations`), tipi di supporto, entità di §9, `DefaultAppPaths` |
| Imaging | Decodifica SkiaSharp; ricampionatore **bit-exact con Pillow** (test con valori di riferimento) |
| Inference | ONNX Runtime 1.31 CPU, tensori senza copia, creazione sessione asincrona |
| Analysis | Adattatore D-FINE (contratto HF) |
| App | Guscio Avalonia 12.1.4: token §12bis Dark/Light, IBM Plex incorporati, barra titolo estesa, navigazione 184/48 (auto a 1280), barra di stato e `KeyBindings` dalla stessa tabella dei comandi, IT/EN, DI, log Serilog via `IAppPaths` |
| Test | 127 test: 119 in CI (Unit + Integration) + 8 di parità locali, saltati senza `PHOTOFLOW_TESTDATA` |
| CI | GitHub Actions: controllo immagini + build/test Windows e Linux, azioni fissate per SHA |
| Laboratorio | uv, torch 2.14.1 cu130, transformers 4.55.4, onnxruntime-gpu 1.31: `fetch_models`, `export_dfine`, `parity_refs`, `benchmark`, `verify_ramplus` |
| Manifest | 7 modelli con URL fissati, SHA-256, dimensioni, licenze verificate alla fonte |

## Set di test reale

240 file in `PHOTOFLOW_TESTDATA/photos`: 178 NEF (Nikon D40 lossy, D5100 lossless), 52 JPG (49 Samsung SM-A500FU), 11 `.xmp` preesistenti (solo letti). **Nessun NEF della Z5 II**: l'utente non potrà fornirli prima dell'implementazione (vedi "Per la fase 1").

## Benchmark (230 foto reali, RTX 3090 / CPU 12 core-24 thread)

| Fase | CUDA ms/foto | CPU ms/foto |
|---|---|---|
| Anteprima (NEF: `JpgFromRaw` via exiftool; JPG: decodifica) | — | 70 |
| D-FINE S / M | 13,3 / 17,0 | 123 / 191 |
| Volti buffalo_l (1,1 volti/foto) | 22,7 | 117 |
| CLIP immagine (quantizzato) | 26,4 | 17,4 |
| RAM++ int8 | 543 | 611 |
| **Stima 1.000 foto** (anteprima + rilevamento + volti + CLIP + RAM++) | **11,3 min** (spec §5: 6-11) | **15,7 min** (spec: 22-50 su 8 core) |

RAM++ int8 occupa ~9 degli 11,3 minuti GPU: la quantizzazione dinamica gira in CPU anche con CUDA (nota in §5). Con varianti fp32/fp16 su GPU la stima scende a pochi minuti (da misurare in fase 3). La CPU di misura ha più core di quella ipotizzata in §5.

## Verifica RAM++ (§13 "verifica RAM++", 52 JPG reali)

ONNX int8 della community vs RAM++ originale PyTorch, stesso tensore d'ingresso: insiemi di tag identici 12/52, Jaccard medio 0,866, max |Δ probabilità| 0,141; la int8 perde tag sistematicamente. Liste di tag e soglie coerenti con l'originale. **Non soddisfa** la parità di §14: in fase 3 si usa fp32 o un export verificato (annotato in §5).

## Decisioni prese durante la fase

| # | Decisione |
|---|---|
| — | `IInferenceProvider.CreateSessionAsync(…, ct)` asincrono (CLAUDE.md §4) |
| T1 | Parità livello tensori: query abbinate (assegnazione ottima) prima del confronto; punteggi ≤ 5e-3, riquadri ≤ 5e-4. Motivo: query quasi a pari merito si scambiano di posizione tra runtime (5-8 foto su 40, 10-22 query) |
| 0.6 | Licenze, rischi R15-R20 e note di laboratorio applicati a spec §5, §15, §16, §17 |

## Per la fase 1 (da trattare nel piano)

- **NEF Z5 II non disponibili**: la "primissima attività della fase 1" (§13) va rinviata a dopo l'implementazione; in fase 1 si lavora con i NEF D40/D5100 e si rende il gestore robusto ai NEF HE (anteprima incorporata, R15).
- **Backend di inferenza** (R16, R17): una sola distribuzione di ONNX Runtime per processo; scegliere tra DirectML NuGet (fermo a 1.24.4), Windows ML (ORT 1.27.1) e CUDA.
- **Binding LibRaw**: P/Invoke sottile sui binari ufficiali 0.22.x.
- Note N3 (D-FINE solo fp32 su CUDA) e N4 (YuNet a ingresso fisso).

## Verifiche manuali per l'utente

1. ✅ App avviata dall'utente il 2026-10-12: si apre e mostra la navigazione (atteso per la fase 0). Facoltativo: pulsanti di sistema, trascinamento e doppio clic sulla barra del titolo, `dotnet run --project src/PhotoFlow.App -- --theme light`.
2. Hook su un altro clone: dopo il clone, `git config core.hooksPath .githooks`.
