# Third-party notices

PhotoFlow is licensed under the Apache License 2.0 (see `LICENSE` and `NOTICE`). This file lists the third-party
components it uses, with their licenses. Versions are those of `Directory.Packages.props` and `build/ExifTool.targets`
at the time of writing. The installed application carries this file, `LICENSE`, `NOTICE` and the notices of Magick.NET
in its `licenses` folder; the LGPL libraries are described in [LGPL libraries in Magick.Native](#lgpl-libraries-in-magicknative)
(review R19, spec §16).

## Bundled with the application

| Component | Version | License | Notes |
| --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.4 | MIT | User interface |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | View models |
| Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Logging | 10.0.12 | MIT | Composition, logging |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4.0, 10.0.0, 7.0.0 | Apache-2.0 | Log files |
| SkiaSharp (Skia) | 3.119.4 | MIT (Skia: BSD-3-Clause) | JPEG decoding, thumbnails |
| Magick.NET-Q8-AnyCPU (ImageMagick 7.1.2-33) | 15.0.0 | Apache-2.0 (ImageMagick License) | Fallback RAW and HEIC decoding. `Magick.Native-Q8-x64.dll` links **statically** libraries under the LGPL: see [below](#lgpl-libraries-in-magicknative); all notices and license texts in `licenses/Magick.NET-Notice.txt` |
| Velopack | 1.2.161 | MIT | Installer, updates and uninstall (spec §11) |
| Microsoft.ML.OnnxRuntime.Gpu | 1.31.0 | MIT | Inference (CPU and CUDA execution providers) |
| Microsoft.ML.OnnxRuntimeGenAI.Cuda | 0.17.1 | MIT | Level D captions, only in the separate `PhotoFlow.Vlm` process (phase 5b, exception U4) |
| Olive recipe for Qwen3.5 (`microsoft/olive-recipes@38e17a606cda`) | — | MIT (`tools/vlm/recipes/qwen3_5/LICENSE`); `codes/modeling_qwen3_5.py`: Apache-2.0 (Qwen Team, Hugging Face) | Conversion scripts shipped as `vlm/`, run with the Python of the user's PC |
| Olive recipe for Qwen2.5-VL (`microsoft/olive-recipes@232aa122155f`) | — | MIT (`tools/vlm/recipes/qwen2_5_vl/LICENSE`); `codes/modeling_qwen2_5_vl.py`: Apache-2.0 (Qwen Team, Hugging Face) | Conversion scripts shipped as `vlm/`, run with the Python of the user's PC |
| Microsoft.Data.Sqlite (SQLitePCLRaw, SQLite) | 10.0.12 | MIT (SQLitePCLRaw: Apache-2.0; SQLite: public domain) | Catalog |
| Dapper | 2.1.89 | Apache-2.0 | Catalog queries |
| System.IO.Hashing | 10.0.12 | MIT | xxHash3 verified copy |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | DPAPI wrapping of the face fingerprint key (Windows) |
| ExifTool by Phil Harvey | 13.59 | Artistic License or GPL (Perl terms) | Separate, unmodified executable shipped next to the application (`tools/exiftool`), run as an external process; the Windows build includes its own Perl runtime under the same terms |
| IBM Plex Sans, IBM Plex Mono | — | SIL Open Font License 1.1 | `src/PhotoFlow.App/Assets/Fonts/OFL.txt` |

## LGPL libraries in Magick.Native

`Magick.Native-Q8-x64.dll` is the native part of Magick.NET, built by the Magick.NET project and redistributed by
PhotoFlow **unmodified**. Inside it, these libraries are linked statically and are licensed under the GNU Lesser General
Public License (the version of each one, and its full text, is in `licenses/Magick.NET-Notice.txt`):

| Library | Version | Source |
| --- | --- | --- |
| libheif | 1.23.6 | https://github.com/strukturag/libheif |
| libde265 | 1.1.3 | https://github.com/strukturag/libde265 |
| LibRaw (LGPL-2.1 or CDDL-1.0) | 0.22.2 | https://github.com/LibRaw/LibRaw |
| glib | 2.64.3 | https://gitlab.gnome.org/GNOME/glib |
| pango | 1.45.3 | https://gitlab.gnome.org/GNOME/pango |
| librsvg | 2.40.20 | https://gitlab.gnome.org/GNOME/librsvg |
| gdk-pixbuf | 2.44.8 | https://gitlab.gnome.org/GNOME/gdk-pixbuf |
| libcroco | 0.6.13 | https://gitlab.gnome.org/Archive/libcroco |
| fribidi | 1.0.17 | https://github.com/fribidi/fribidi |
| liblqr | 0.4.2 | https://github.com/carlobaldassi/liblqr |

- **Replacing the library.** PhotoFlow loads `Magick.Native-Q8-x64.dll` at run time as a separate shared library: it can
  be replaced with a version rebuilt from modified sources of these libraries. The build scripts of Magick.Native, which
  pin the versions above, are public (https://github.com/dlemstra/Magick.Native, Apache-2.0).
- **Written offer.** For at least three years from the distribution of each PhotoFlow version, Foyer Labs provides on
  request, at no charge beyond the cost of the medium, the complete corresponding source code of the LGPL libraries
  distributed with that version. Ask by opening an issue at https://github.com/foyer-labs/PhotoFlow/issues.
- PhotoFlow's own license (Apache-2.0) does not restrict modifying these libraries or reverse engineering to debug such
  modifications.
- HEIC photos are decoded with libheif and libde265 (HEVC). Patent licensing of HEVC is a separate matter from these
  copyright licenses and is not covered by this file.

## Downloaded at first use (never bundled)

| Component | License | Conditions |
| --- | --- | --- |
| NVIDIA CUDA runtime libraries (cudart, cuBLAS, cuFFT, cuRAND, NVRTC, nvJitLink) and cuDNN — the "GPU pack" (`models/gpu-runtime.json`) | NVIDIA CUDA Toolkit EULA, NVIDIA cuDNN Software License Agreement | Downloaded from NVIDIA's redistribution archives only after the user accepts the licenses in the first run; verified with SHA-256; kept unmodified with their `LICENSE.txt`; used only by PhotoFlow and only on NVIDIA GPUs |
| Python packages of the level D and AdaFace conversions (`tools/vlm/requirements.txt`: PyTorch, transformers, ONNX, ONNX Runtime, ONNX Runtime GenAI, Olive) | BSD-3-Clause, Apache-2.0, MIT | Installed by `pip` from PyPI and the PyTorch index into a private environment of PhotoFlow, only when the user starts the conversion; never bundled |
| Qwen3.5-9B original weights | Apache-2.0 | Downloaded from Hugging Face into the models folder; PhotoFlow verifies them (SHA-256) and converts them on the PC |
| AdaFace IR-101 WebFace12M original weights (`minchul/cvlface_adaface_ir101_webface12m`) | AdaFace code MIT; the weights follow the license of the training dataset (WebFace), treated as non-commercial | Optional; downloaded from Hugging Face only after the user accepts the license; verified (SHA-256) and converted to ONNX on the PC with the environment of the level D conversion (`tools/vlm/recipes/adaface_ir101`, PhotoFlow's own code) |
| Qwen2.5-VL-7B-Instruct original weights | Apache-2.0 | Downloaded by the user from Hugging Face; PhotoFlow verifies them (SHA-256) and converts them on the PC |
| Machine-learning models (`models/manifest.json`) | Per model, shown before download (Apache-2.0, MIT; InsightFace buffalo_l: non-commercial research only) | Downloaded with SHA-256 verification; licenses that require it (buffalo_l) are accepted explicitly before the download |

## Development only (not distributed)

xUnit v3 (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), Avalonia.Headless.XUnit (MIT); the Python lab in `tools/lab`
(its own `pyproject.toml`, outputs outside the repository).
