# Report di fine fase 2 — Raffiche, Revisione completa, Persone

Data: 2026-10-13 · Branch: `phase/2-review-people` · Piano: [`docs/plans/fase-2.md`](../plans/fase-2.md)

Fase svolta in autonomia su indicazione dell'utente (2026-10-12): le decisioni prese durante la fase sono in `docs/progress.md` e nella specifica (§5, §6, §7, §17) e sono riassunte sotto.

## Checklist dei criteri di accettazione (§13)

| # | Criterio | Esito | Evidenza |
|---|---|---|---|
| 1 | Bracketing e timelapse mai classificati come raffiche nel set di test | ✅ Superato (test) · 🔍 verifica a mano sul set reale | `SequenceGrouperTests`: `Exposure_bracketing_is_never_a_burst`, `A_timelapse_is_never_a_burst_even_when_shots_are_close_and_similar` (con e senza decimi di secondo), `A_long_fast_burst_stays_a_burst`, due fotocamere insieme, foto senza data o senza embedding; `GroupingStageTests.Bursts_are_ranked_special_sequences_are_not_and_user_decisions_stay` su JPEG con EXIF reali scritti da exiftool (bracketing ±EV a 0,2 s, timelapse, raffiche, cambio di soggetto): nessun bracketing o timelapse dentro una raffica, sequenze speciali senza classifica, decisioni dell'utente intatte. Sul set locale (230 foto) PhotoFlow trova 18 raffiche (37 foto) e 1 bracketing (3 foto): il set non ha un riferimento etichettato di sequenze, quindi va guardato a mano (punto 1 sotto) |
| 2 | Revisione interamente utilizzabile da tastiera | ✅ Superato | `ReviewKeyboardTests.A_review_is_completed_with_the_keyboard_only`: analisi reale sul posto di raffiche con EXIF, poi tutta la revisione solo con tasti (navigazione, P/X/stelle, annulla, confronto 2-4 con zoom e sincronizzazione, raffiche, filtri, taglie, pannello, conferma e scrittura XMP). La stessa prova per le nuove viste: `PeopleKeyboardTests` (seleziona, unisci, rinomina scrivendo nel campo senza attivare i comandi, pagina persona, separa, "non è questa persona", Esc) e `ProfilesKeyboardTests` (Ctrl D, Esc, Ctrl S). Ogni comando viene da un'unica tabella per vista da cui nascono `KeyBindings` e barra di stato (`CommandTableTests`) |
| 3 | Chiave delle impronte rimossa: ricalcolo riuscito con assegnazioni preservate | ✅ Superato | `PeopleEngineTests.Without_the_key_recognition_waits_and_a_recompute_keeps_every_assignment`: catalogo con persone e assegnazioni manuali, file della chiave tolto → riconoscimento sospeso (P13), ricalcolo dalle anteprime con una nuova chiave → stesse righe, stesse persone, stesse decisioni manuali, riconoscimento di nuovo attivo. `FingerprintProtectionTests`: impronta mai in chiaro nel catalogo, chiave o blob alterati illeggibili, password che apre la chiave su un altro account, DPAPI su Windows. Interfaccia: banner 4c3 con "Inserisci password" e "Ricalcola impronte" (`PhotoFlowHost.RecomputeFingerprintsAsync`); password impostabile nelle Impostazioni (`SettingsTests`) |
| 4 | Metriche del culling calcolate dal set etichettato con margine di incertezza | ✅ Superato (calcolo) · ⏳ misura in attesa del set etichettato | `CullingMetricsTests`: conteggi delle tre metriche (scarti sbagliati < 2%, intercettati > 70%, da rivedere < 20%), intervalli di Wilson al 95% contro valori di riferimento, regola D23 (modalità normale solo con i tre intervalli interamente entro obiettivo). Modalità etichettatura (2d) con pannello delle metriche; Impostazioni › Culling con le tre metriche e la modalità normale abilitata solo quando D23 lo consente (`SettingsTests.Normal_mode_stays_locked_without_metrics_and_the_password_opens_the_key`). La misura reale aspetta le tue etichette (D20): finché non ci sono, resta la modalità prudente |

Test: 377, di cui 33 locali (parità, qualità, prestazioni) saltati senza `PHOTOFLOW_TESTDATA`; gli altri girano in CI su Windows e Linux (344 superati in locale prima della PR). Con `PHOTOFLOW_TESTDATA=E:\AI\PhotoFlowTestData` (Release, CUDA) i test locali di parità e qualità passano tutti (28: D-FINE, buffalo\_l, YuNet, CLIP, impronte ArcFace/SFace, HDBSCAN contro scikit-learn, segnali OpenCV); resta saltato solo quello sul set etichettato, che non c'è ancora.

## Cosa è stato fatto (tappe 2.0-2.12)

| Tappa | Contenuto |
|---|---|
| 2.0 | Test prima del codice: I5 per gruppi, volti e persone (`GroupingStageTests`, `PeopleEngineTests`, `ProfileStageTests`: scelte dell'utente mai sostituite); impronte mai in chiaro (`FingerprintProtectionTests`); chiave rimossa; sequenze speciali mai raffiche |
| 2.1 | CLIP ViT-B/32 fp32 (F2-1) in C#: preparazione come Pillow, tokenizzatore BPE, embedding immagine e testo; parità con l'originale PyTorch (`ClipParityTests`: tensore d'ingresso identico, coseno 1,0); tabella `Embeddings` |
| 2.2-2.3 | Raggruppamenti come barriera di lotto (`IBatchStage`): raffiche per fotocamera, bracketing, timelapse, gruppi manuali; classifica nella raffica con i pesi del profilo, pari merito → da rivedere; decisione per gruppo rifatta dai segnali salvati; profilo per gruppo con CLIP zero-shot (D8: automatico con riserva o suggerimento), "Rianalizza come…" |
| 2.4-2.5 | Impronte ArcFace (buffalo\_l) e SFace con allineamento portato da OpenCV (parità ≥ 0,99999); cifratura AES-256-GCM, chiave con DPAPI e password opzionale (PBKDF2 600k) |
| 2.6 | Riconoscimento con campioni per anno (P12), HDBSCAN in C# (F2-2, F2-6; parità con scikit-learn), "Persona N", ricalcolo delle impronte |
| 2.7 | Revisione completa (2a-2h): griglia per raffiche, filtri, taglie, confronto 2-4 con zoom sincronizzato, semantica di P (P1), decisioni durante l'analisi (P2), cambio profilo per gruppo, conferma con passaggio a Persone, errore parziale con "Riprova" e "Solo catalogo per questi" (P4), "Sposta scarti…" in `_scarti` (P6, F2-5), vista Numeri visibile e disattivata |
| 2.8 | **Persone** (4a, 4b, 4c, 4c2, 4c3): gruppi per dimensione con filtri Nuovi/Senza nome/Con nome/Ignorate, selezione multipla, unisci (M), rinomina con suggerimenti (F2; un nome conferma i volti come campioni), ignora (I), pagina persona con volti per anno (prima i meno simili, bordo tratteggiato sotto soglia), separa (S), sposta a (Ctrl M), "non è questa persona" (Canc), copertina, elimina persona con le impronte; stato vuoto, raggruppamento in corso, chiave non disponibile. **XMP**: regioni MWG con nome e `Persone\|Nome` solo per foto confermate, regioni di altri programmi conservate, nessuna regione duplicata a una seconda scrittura (`PeopleXmpTests`); un RAW riceve le persone del JPEG gemello |
| 2.9 | Etichettatura (2d) con sessioni alla cieca, metriche con intervallo, `Corrections` con i segnali dell'epoca; modalità normale sbloccata solo da D23 |
| 2.10 | **Profili** (5e): predefiniti bloccati, duplica (Ctrl D), modifica di pesi (somma 100), soglie di mosso, esposizione, zona di nitidezza, parametri P17 e volti, valori del profilo d'origine accanto, annulla (Esc), salva come nuova versione (Ctrl S) in `profiles/custom-*.v<n>.json`; suggerimento della soglia di mosso dalle correzioni (`ThresholdAdvisor`, mai applicato da solo); tutta l'applicazione usa la `ProfileLibrary` (pipeline, revisione, import, librerie, etichettatura) |
| 2.11 | **Benchmark in-app** (5f): ~50 foto delle librerie in sola lettura, fasi misurate una foto alla volta (anteprima, segnali + soggetti, volti + impronte, CLIP) con RAM e VRAM di picco, poi l'analisi completa in parallelo; stime per un import di 1.000 scatti e per l'archivio; risultato conservato nelle impostazioni (`BenchmarkTests`) |
| 2.12 | Misure locali (sotto); rianalisi del profilo automatico più veloce (solo se cambia la "chiave di misura", in parallelo) |

Anche: procedura d'import con "Riconoscimento" e profilo automatico con riserva; analisi d'archivio con la modalità volti dell'import; nelle viste, i tasti di un solo carattere non attivano comandi mentre si scrive in un campo.

## Misure

**Set locale** `PHOTOFLOW_TESTDATA/photos` (230 foto: 178 NEF, 52 JPEG), Release, RTX 3090, CUDA, 24 thread, 4 analisi in parallelo (background), stessa sessione per le due misure:

| Misura | Fase 1 (`ArchivePerformanceTests`) | Fase 2 (`Phase2PerformanceTests`) |
|---|---|---|
| Analisi per foto | D-FINE M, volti/occhi buffalo\_l, decisione | + CLIP immagine, impronte ArcFace cifrate |
| EXIF + anteprima + analisi | 31,1 s → **7,4 foto/s** | 33,2 s → **6,9 foto/s** (−7%) |
| Raggruppamento + profilo CLIP per gruppo | — | 0,6 s |
| Rianalisi con il profilo del gruppo (modalità automatica) | — | 17,2 s (prima dell'ottimizzazione: ~56 s) |
| Decisione per gruppo | — | 0,7 s |
| Riconoscimento e clustering | — | 0,5 s |
| Stima per 57.130 foto | 2,1 h | 2,3 h in modalità manuale, 3,6 h in automatica |

Risultati sul set: 18 raffiche (37 foto), 1 bracketing (3 foto); 314 volti, 259 con impronta (gli altri sotto i 48 px), 180 assegnati a 38 gruppi "Persona N"; profili assegnati da CLIP: motorsport 84, eventi 76, paesaggio 58, animali 10, ritratto 2. Il report con la scena di ogni foto (per il controllo a mano) è in `PhotoFlowTestData\lab\performance\phase2-*.md`.

## Decisioni tecniche prese durante la fase

Registrate in `docs/progress.md` e nella specifica:

- **F2-1 … F2-6** del piano (CLIP fp32; HDBSCAN in C#; DPAPI + password; raggruppamenti idempotenti; "Sposta scarti" solo nella stessa libreria; selezione EOM con `min_samples` 2).
- **Regole delle sequenze**: bracketing ≥ 3 scatti con compensazione su ≥ 0,3 EV o EV100 su ≥ 1 EV; timelapse ≥ 10 scatti a intervallo ≥ 1 s costante entro ±10% (±1 s senza decimi di secondo); raffiche per fotocamera (§6).
- **Soglie del riconoscimento**: ArcFace 0,45, SFace 0,363, margine sul secondo candidato 0,05, fascia di suggerimento 0,10, fino a 5 campioni per persona per anno, nessuna impronta sotto 48 px (§7).
- **Regioni MWG** scritte da exiftool come struttura completa, conservando quelle di altri programmi; un RAW riceve le persone del JPEG gemello (§7).
- **Profili personalizzati** come file versionati `custom-<nome>.v<n>.json`, versioni precedenti conservate, profilo mancante sostituito da Eventi (§6).
- **Rianalisi** solo quando cambia la chiave di misura del profilo (zone, soggetti, volti, soglie degli occhi), in parallelo (§6).
- **Suggerimento delle soglie** dalle correzioni: ≥ 30 correzioni, scarti sbagliati con peso 3, solo la soglia di mosso per ora (§6).
- **Benchmark**: la VRAM si legge da `nvidia-smi` (tutti i processi, NVIDIA soltanto); i modelli restano caricati per la sessione invece di caricarsi fase per fase come suggerisce la tavola 5f (backlog).
- **Tasti di un carattere** disattivati mentre il fuoco è in un campo di testo (altrimenti scrivere "Mia" in Rinomina avrebbe unito i gruppi con M e ignorato con I).

**Scostamenti dalle tavole** (nel backlog §17): in Persone mancano l'annulla (Ctrl Z) di unisci/separa/rinomina, la selezione a rettangolo e "Apri foto" dalla pagina persona; la VRAM per fase è di tutto il sistema; non si può ancora eliminare un profilo personalizzato.

## Da verificare a mano (per te)

1. **Sequenze sul set reale**: avvia PhotoFlow, Librerie → la cartella delle tue foto → analisi, poi Revisione: le intestazioni "Raffica", "Bracketing", "Sequenza" devono corrispondere a ciò che hai scattato (nessun bracketing o timelapse dentro una raffica).
2. **Persone**: con buffalo\_l o SFace installati e la modalità "Riconoscimento" nell'import, apri Persone dopo un import: rinomina un gruppo (F2, Invio), uniscine due (Spazio su ciascuno, poi M), apri la pagina persona (Invio), separa un volto (S). **La licenza di buffalo\_l la accetti tu** nelle Impostazioni › Modelli; PhotoFlow non la accetta mai al posto tuo.
3. **XMP con le persone**: dopo la conferma della revisione, apri una foto con un nome in Photoshop/Bridge/Lightroom: keyword `Nome` e `Persone|Nome`, regione del volto con il nome.
4. **Chiave delle impronte**: Impostazioni › Catalogo → imposta una password; sposta temporaneamente `%LOCALAPPDATA%\PhotoFlow\config\face-key.json` su un altro account o PC con lo stesso catalogo → Persone mostra il banner; la password sblocca senza ricalcolo.
5. **Profili**: Profili → Eventi → Ctrl D → cambia pesi e soglie → Ctrl S; il file `custom-…v2.json` compare in `%LOCALAPPDATA%\PhotoFlow\config\profiles`; il nuovo profilo compare nell'import.
6. **Benchmark**: Impostazioni › Hardware → "Esegui benchmark": tabella per fase e stime; nessun file nuovo nelle librerie.
7. **Scene CLIP**: scorri `PhotoFlowTestData\lab\performance\phase2-*.md` e segnala le scene evidentemente sbagliate.
8. **Set etichettato** (D20): in Revisione premi L per l'etichettatura; quando le tre metriche sono entro obiettivo, Impostazioni › Culling abilita la modalità normale.
9. Restano validi i punti manuali delle fasi 1 e 1b (scheda SD vera, NEF Z5 II, subset d'archivio, XMP in Esplora risorse).

## Limiti noti e rinvii

La modalità automatica costa ~55% in più dell'analisi sul set locale: classificare la scena per foto prima dell'analisi è nel backlog. Le metriche reali del culling aspettano il set etichettato. Le scene CLIP sono zero-shot: il controllo sul tuo archivio dirà se servono frasi migliori per qualche profilo.
