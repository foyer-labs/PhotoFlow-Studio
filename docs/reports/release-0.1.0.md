# Report di rilascio — PhotoFlow 0.1.0

Data: 13 ottobre 2026 · Tag `v0.1.0` · Release GitHub non pre-release ("Latest")

## Contenuto

| Area | Cosa | Evidenza |
|---|---|---|
| Versione | `0.1.0` in `Directory.Build.props`; mostrata in Impostazioni › Generali | Build |
| Installer | Velopack 1.2.161 (MIT), id `FoyerLabs.PhotoFlow`, self-contained win-x64; Setup rinominato `PhotoFlow-0.1.0-Setup.exe` | `vpk pack` |
| Aggiornamenti | Feed dalle release GitHub del repository (solo release non pre-release); controllo all'avvio disattivabile; installazione solo dalle Impostazioni e mai con un job in corso | `AppUpdates`, spec §11 |
| Disinstallazione | Toglie programma, cache e temporanei; restano catalogo, modelli, impostazioni, backup e log | `UninstallCleanupTests` |
| Contenuto del pacchetto | Applicazione, `PhotoFlow.Vlm`, script `vlm/`, exiftool, cartella `licenses/` (LICENSE, NOTICE, THIRD-PARTY-NOTICES, note di Magick.NET); **nessun modello** e **nessuna libreria NVIDIA** (pacchetto GPU scaricato al primo avvio) | Ispezione della cartella pubblicata |
| Licenze (R19) | Librerie LGPL di Magick.Native elencate con versioni e sorgenti, libreria sostituibile, offerta scritta dei sorgenti | `THIRD-PARTY-NOTICES.md`, spec §16 |
| Documentazione | README, guida utente, FAQ, note di rilascio, testi per l'annuncio, tutto in italiano; 13 screenshot da un catalogo dimostrativo di immagini disegnate (nessuna foto reale) | `docs/guida/`, `docs/release/`, `DocsScreenshots.cs` |
| Test | Unit, Integration, Scenario verdi in locale e in CI (Windows e Linux) | CI delle PR #9 e del rilascio |

## Limiti dichiarati

Solo Windows; installer non firmato (avviso SmartScreen); modelli da scaricare a parte; buffalo_l non commerciale; didascalie italiane con NVIDIA ≥ 12 GB. I brevetti HEVC (HEIC) non sono coperti dalla revisione delle licenze (R19): la revisione non è un parere legale.

## Verifiche manuali (per l'utente, non dichiarate superate)

1. **Installazione**: scaricare `PhotoFlow-0.1.0-Setup.exe` dalla release, avviarlo; SmartScreen → «Ulteriori informazioni» › «Esegui comunque». PhotoFlow si apre; c'è il collegamento nel menu Start.
2. **Dati esistenti**: con un catalogo già creato dalla cartella di sviluppo, la versione installata lo ritrova (`%LOCALAPPDATA%\PhotoFlow`, o la posizione scelta) con impostazioni e modelli.
3. **Versione**: Impostazioni › Generali mostra «PhotoFlow 0.1.0»; «Controlla ora» risponde «Hai l'ultima versione».
4. **Aggiornamento** (alla prossima versione): la barra di stato annuncia la nuova versione; «Scarica, installa e riavvia» riapre PhotoFlow aggiornato con catalogo e impostazioni intatti.
5. **Disinstallazione**: da «App installate»; la cartella di installazione sparisce, `%LOCALAPPDATA%\PhotoFlow` resta con catalogo, modelli, backup e log, senza `cache` e `temp`.
6. **Pacchetto GPU** da installato: il pacchetto si scarica e la GPU viene usata dopo il riavvio.
