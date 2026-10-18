# -*- mode: python ; coding: utf-8 -*-
# Build:  pyinstaller packaging/denoiser.spec --noconfirm
# Produce dist/Denoiser/ con Denoiser.exe (interfaccia) e denoiser-cli.exe (riga di comando).

import os
import re
import sys

from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs, collect_submodules

# I percorsi sono relativi alla cartella di questo file (SPECPATH).
SRC = os.path.join(SPECPATH, "..", "src")

hiddenimports = (
    collect_submodules("skimage.restoration")
    + collect_submodules("pywt")
    # Solo il motore di inferenza: transformers/quantization/tools di onnxruntime sono strumenti
    # di sviluppo (centinaia di moduli con dipendenze pesanti) che l'app non usa.
    + ["onnxruntime"] + collect_submodules("onnxruntime.capi")
    + ["onnx", "onnx.helper", "onnx.numpy_helper", "onnx.checker"]
)
datas = collect_data_files("skimage") + collect_data_files("onnxruntime")
# onnxruntime (e DirectML su Windows) vive in librerie dinamiche da includere esplicitamente.
binaries = collect_dynamic_libs("onnxruntime")
excludes = ["onnxruntime.transformers", "onnxruntime.quantization", "onnxruntime.tools", "torch",
            "tkinter", "matplotlib", "PySide6.QtWebEngineCore", "PySide6.QtWebEngineWidgets", "PySide6.Qt3DCore"]


def analysis(script):
    return Analysis(
        [script],
        pathex=[SRC],
        hiddenimports=hiddenimports,
        datas=datas,
        binaries=binaries,
        excludes=excludes,
        noarchive=False,
    )


# Versione letta dal pacchetto: compare nelle proprietà del file in Esplora risorse.
with open(os.path.join(SRC, "denoiser", "__init__.py"), encoding="utf-8") as f:
    VERSION = re.search(r'__version__ = "([^"]+)"', f.read()).group(1)
_numbers = tuple(int(x) for x in (re.findall(r"\d+", VERSION) + ["0"] * 4)[:4])
ICON = os.path.join(SPECPATH, "denoiser.ico")


def version_info(description):
    if sys.platform != "win32":
        return None  # le informazioni di versione esistono solo negli eseguibili Windows
    from PyInstaller.utils.win32.versioninfo import (
        FixedFileInfo, StringFileInfo, StringStruct, StringTable, VarFileInfo, VarStruct, VSVersionInfo,
    )

    return VSVersionInfo(
        ffi=FixedFileInfo(filevers=_numbers, prodvers=_numbers),
        kids=[
            StringFileInfo([StringTable("041004B0", [
                StringStruct("FileDescription", description),
                StringStruct("FileVersion", VERSION),
                StringStruct("ProductName", "Denoiser"),
                StringStruct("ProductVersion", VERSION),
                StringStruct("LegalCopyright", "Copyright (c) 2026 foyer-labs - licenza MIT"),
            ])]),
            VarFileInfo([VarStruct("Translation", [0x0410, 1200])]),
        ],
    )


gui = analysis(os.path.join(SPECPATH, "denoiser_gui.py"))
cli = analysis(os.path.join(SPECPATH, "denoiser_cli.py"))

gui_exe = EXE(
    PYZ(gui.pure),
    gui.scripts,
    [],
    exclude_binaries=True,
    name="Denoiser",
    console=False,
    upx=False,
    icon=ICON,
    version=version_info("Denoiser - riduzione del rumore delle foto"),
)
cli_exe = EXE(
    PYZ(cli.pure),
    cli.scripts,
    [],
    exclude_binaries=True,
    name="denoiser-cli",
    console=True,
    upx=False,
    icon=ICON,
    version=version_info("Denoiser - riga di comando"),
)
coll = COLLECT(
    gui_exe,
    gui.binaries,
    gui.datas,
    cli_exe,
    cli.binaries,
    cli.datas,
    name="Denoiser",
    upx=False,
)
