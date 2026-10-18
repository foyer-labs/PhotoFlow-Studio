# Licenze di terze parti

[← Torna al README](README.md)

Il codice di Denoiser è distribuito con [licenza MIT](LICENSE). Denoiser usa componenti di altri autori, ciascuno con la propria licenza. Il pacchetto per Windows
li include (tabella 1). I modelli neurali non sono inclusi: vengono scaricati al primo uso
dalle pubblicazioni ufficiali (tabella 2). I testi completi delle licenze si trovano nei rispettivi
progetti e, per le librerie Python, nelle cartelle `*.dist-info` del pacchetto.

## 1. Componenti inclusi nel pacchetto per Windows

| Componente | Uso | Licenza |
|---|---|---|
| [Python](https://www.python.org) | Interprete | PSF License |
| [Qt 6](https://www.qt.io) / [PySide6](https://doc.qt.io/qtforpython-6/) | Interfaccia grafica | LGPL-3.0 |
| [LibRaw](https://www.libraw.org) (tramite [rawpy](https://github.com/letmaik/rawpy)) | Lettura e sviluppo dei RAW | LibRaw: LGPL-2.1 o CDDL-1.0; rawpy: MIT |
| [NumPy](https://numpy.org) | Calcolo numerico | BSD-3-Clause |
| [SciPy](https://scipy.org) | Calcolo scientifico (dipendenza di scikit-image) | BSD-3-Clause |
| [OpenCV](https://opencv.org) (opencv-python-headless) | Non-Local Means, filtri, ridimensionamenti | Apache-2.0 (le parti di FFmpeg incluse nel pacchetto: LGPL-2.1) |
| [scikit-image](https://scikit-image.org) | Denoise wavelet | BSD-3-Clause |
| [PyWavelets](https://pywavelets.readthedocs.io) | Trasformate wavelet | MIT |
| [tifffile](https://github.com/cgohlke/tifffile) | Lettura e scrittura di TIFF e DNG | BSD-3-Clause |
| [Pillow](https://python-pillow.org) | JPEG, PNG, EXIF, profili ICC | MIT-CMU (HPND) |
| [ONNX](https://onnx.ai) e [Protocol Buffers](https://protobuf.dev) | Costruzione dei modelli neurali | Apache-2.0; BSD-3-Clause |
| [ONNX Runtime](https://onnxruntime.ai) con DirectML | Esecuzione delle reti neurali sulla scheda video | MIT; DirectML: licenza Microsoft (componente ridistribuibile) |
| [ExifTool](https://exiftool.org) di Phil Harvey (con l'interprete Perl incluso) | Copia dei metadati | Stessi termini di Perl: Artistic License o GPL |
| [PyInstaller](https://pyinstaller.org) (solo il programma di avvio) | Creazione dell'eseguibile | GPL-2.0 con eccezione per il programma di avvio |

Le librerie Qt sono distribuite come librerie dinamiche separate (file `Qt6*.dll`), che possono
essere sostituite con versioni compatibili.

## 2. Modelli neurali (scaricati al primo uso, non inclusi)

| Modello | Autori | Fonte | Licenza |
|---|---|---|---|
| DRUNet (`drunet_color.pth`, 125 MB) | Kai Zhang et al., *Plug-and-Play Image Restoration with Deep Denoiser Prior*, IEEE TPAMI 2021 | [cszn/KAIR](https://github.com/cszn/KAIR) | MIT |
| SCUNet (`scunet_color_real_psnr.pth`, 69 MB) | Kai Zhang et al., *Practical Blind Image Denoising via Swin-Conv-UNet and Data Synthesis*, Machine Intelligence Research 2023 | [cszn/SCUNet](https://github.com/cszn/SCUNet), file da [KAIR](https://github.com/cszn/KAIR/releases) | Apache-2.0 |
| Restormer (`real_denoising.pth`, 100 MB) | Syed Waqas Zamir et al., *Restormer: Efficient Transformer for High-Resolution Image Restoration*, CVPR 2022 | [swz30/Restormer](https://github.com/swz30/Restormer/releases) | MIT |
| RawNIND (`rawdenoise-nind.dtmodel`, 55 MB: modelli UtNet2 «bayer» e «lineare» in ONNX) | Benoit Brummer e Christophe De Vleeschouwer (UCLouvain), *Learning Joint Denoising, Demosaicing, and Compression from the Raw Natural Image Noise Dataset*, 2025 ([arXiv:2501.08924](https://arxiv.org/abs/2501.08924)) | Pesi: [trougnouf/rawnind_jddc](https://github.com/trougnouf/rawnind_jddc); conversione ONNX: [darktable-org/darktable-ai](https://github.com/darktable-org/darktable-ai/releases) | Pesi con doppia licenza CC BY 4.0 e GPL-3.0 (usati qui secondo CC BY 4.0, con attribuzione); il pacchetto ONNX di darktable è indicato come GPL-3.0 |

Denoiser non ridistribuisce i modelli: li scarica sul computer dell'utente dalla fonte indicata. Il codice
di Denoiser che li esegue è scritto in proprio (licenza MIT) e non contiene codice dei progetti originali.

L'integrità di ogni file viene verificata con un'impronta SHA-256 prima dell'uso.

## 3. Metodi descritti in letteratura

- Trasformata di Anscombe generalizzata e inversa esatta non distorta: M. Mäkitalo, A. Foi,
  *Optimal inversion of the generalized Anscombe transformation for Poisson-Gaussian noise*,
  IEEE TIP 2013.
- Stima del rumore: J. Immerkær, *Fast noise variance estimation*, CVIU 1996.
- BayesShrink: S. G. Chang, B. Yu, M. Vetterli, *Adaptive wavelet thresholding for image denoising
  and compression*, IEEE TIP 2000.
- Non-Local Means: A. Buades, B. Coll, J.-M. Morel, *A non-local algorithm for image denoising*,
  CVPR 2005.

## 4. Immagini usate nella documentazione

Le schermate mostrano immagini di esempio incluse in scikit-image, senza restrizioni di copyright:
*astronaut* (Eileen Collins, NASA, pubblico dominio), *coffee* (Rachel Michetti, CC0),
*chelsea* (Stefan van der Walt, CC0), *rocket* (SpaceX, pubblico dominio). Il rumore è stato
aggiunto in modo sintetico.
