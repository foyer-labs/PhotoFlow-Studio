# Riga di comando

[← Torna al README](../README.md)

Nel pacchetto per Windows c'è `denoiser-cli.exe`, accanto a `Denoiser.exe`. Installando il progetto
con `pip` il comando si chiama `denoiser`. Le opzioni sono le stesse.

```
denoiser-cli elabora FOTO_O_CARTELLE... [opzioni]   # riduce il rumore e salva
denoiser-cli analizza FOTO                          # informazioni e rumore stimato
denoiser-cli algoritmi                              # metodi e relativi parametri
denoiser-cli modelli elenca | scarica [drunet|scunet|restormer|rawnind|tutti]
denoiser-cli preset elenca | salva NOME [opzioni]
denoiser-cli --version
```

## Esempi

```bat
:: Tutte le foto di una cartella con il preset per ISO alti, in TIFF 16 bit
denoiser-cli elabora D:\Foto\Serata --preset "Forte (ISO alti)" --formato tiff

:: Una foto con NLM, meno grana tolta e più colore, finestra di ricerca più ampia
denoiser-cli elabora DSC_0001.NEF --algoritmo nlm --luminanza 0.8 --colore 1.2 --param search_window=15

:: Cartelle e sottocartelle, JPEG qualità 92, nome con metodo e ISO
denoiser-cli elabora D:\Foto --ricorsivo --formato jpeg --qualita 92 --nome "{nome}_{metodo}_{iso}"

:: DNG ripulito con DRUNet, da sviluppare poi in Lightroom
denoiser-cli elabora DSC_0001.NEF --algoritmo drunet --formato dng

:: Salvare un preset personale e usarlo
denoiser-cli preset salva "Notturne Z5" --algoritmo drunet --colore 1.3 --formato dng
denoiser-cli elabora D:\Foto\Notte --preset "Notturne Z5"
```

## Opzioni di `elabora`

**Riduzione del rumore**

| Opzione | Significato |
|---|---|
| `--preset NOME` | Nome di un preset (`preset elenca`) o percorso di un file `.json` |
| `--algoritmo` | `wavelet` (predefinito), `nlm`, `bilateral`, `drunet`, `scunet`, `restormer`, `rawnind` |
| `--luminanza F` | Forza sulla grana: 0 = disattivata, 1 = rumore misurato |
| `--colore F` | Forza sulle macchie di colore |
| `--dettaglio F` | Dettaglio originale reintrodotto, da 0 a 1 |
| `--pixel-caldi S` | Soglia dei pixel caldi in multipli del rumore (0 = no; tipico 8) |
| `--param CHIAVE=VALORE` | Parametro specifico del metodo (vedi `algoritmi`); ripetibile |

**RAW**

| Opzione | Significato |
|---|---|
| `--livello 3` / `--livello 2` | Denoise sui dati del sensore (predefinito) o dopo lo sviluppo |
| `--bilanciamento` | `camera` (come scattato), `auto`, `daylight`, `temperature`, `custom` |
| `--temperatura K`, `--tinta T` | Temperatura (2000–15000 K) e tinta (−150…150) della luce (implicano `temperature`) |
| `--moltiplicatori R G B` | Moltiplicatori del bilanciamento (implica `custom`) |
| `--demosaic` | `AHD` (predefinito), `AAHD`, `DCB`, `DHT`, `PPG`, `VNG`, `LINEAR` |
| `--esposizione EV` | Correzione di esposizione |
| `--no-luminosita-auto` | Disattiva la luminosità automatica |
| `--dng-converter PERCORSO` / `--no-dng-converter` | Adobe DNG Converter per i RAW non letti da LibRaw |

**Uscita**

| Opzione | Significato |
|---|---|
| `--formato` | `tiff` (predefinito), `png`, `jpeg`, `dng` (solo da RAW) |
| `--bit 8` / `--bit 16` | Profondità di TIFF e PNG |
| `--qualita N` | Qualità JPEG da 1 a 100 |
| `--cartella C` | Cartella di uscita, relativa alla foto o assoluta (predefinita `denoised`) |
| `--nome MODELLO` | Nome del file, con `{nome}`, `{metodo}`, `{iso}`, `{data}` |
| `--sovrascrivi` | Sovrascrive i file esistenti (altrimenti aggiunge `_1`, `_2`, …) |
| `--ricorsivo` | Include le sottocartelle (le cartelle di uscita vengono saltate) |

Due foto con lo stesso nome (es. `IMG_1.NEF` e `IMG_1.JPG`) non si sovrascrivono mai a vicenda nella
stessa esecuzione. Il file originale non viene mai sovrascritto.

## Codici di uscita

| Codice | Significato |
|---|---|
| 0 | Tutte le foto elaborate |
| 1 | Nessuna foto trovata, oppure foto non leggibile (`analizza`) |
| 2 | Almeno una foto non elaborata (gli errori sono indicati per ciascuna) |

## Variabili d'ambiente

| Variabile | Effetto |
|---|---|
| `DENOISER_MODELS_DIR` | Cartella dei modelli neurali (predefinita `%LOCALAPPDATA%\Denoiser\modelli`) |
| `DENOISER_DNG_CONVERTER` | Percorso di Adobe DNG Converter |
