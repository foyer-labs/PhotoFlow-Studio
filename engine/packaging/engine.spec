# -*- mode: python ; coding: utf-8 -*-
# Motore di sviluppo e riduzione del rumore per PhotoFlow Studio (senza interfaccia Qt).
# Build:  pyinstaller packaging/engine.spec --noconfirm
# Produce dist/engine/ con photoflow-engine.exe (motore a richieste, avviato da PhotoFlow Studio)
# e denoiser-cli.exe (la riga di comando di Denoiser).

import os
import re
import sys

from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs, collect_submodules

SRC = os.path.join(SPECPATH, "..", "src")

hiddenimports = (
    collect_submodules("skimage.restoration")
    + collect_submodules("pywt")
    + ["onnxruntime"] + collect_submodules("onnxruntime.capi")
    + ["onnx", "onnx.helper", "onnx.numpy_helper", "onnx.checker"]
)
datas = collect_data_files("skimage") + collect_data_files("onnxruntime")
binaries = collect_dynamic_libs("onnxruntime")
# Niente Qt: l'interfaccia è quella di PhotoFlow Studio.
excludes = ["onnxruntime.transformers", "onnxruntime.quantization", "onnxruntime.tools", "torch",
            "tkinter", "matplotlib", "PySide6", "shiboken6", "denoiser.gui"]


def analysis(script):
    return Analysis([script], pathex=[SRC], hiddenimports=hiddenimports, datas=datas, binaries=binaries,
                    excludes=excludes, noarchive=False)


with open(os.path.join(SRC, "denoiser", "__init__.py"), encoding="utf-8") as f:
    VERSION = re.search(r'__version__ = "([^"]+)"', f.read()).group(1)
_numbers = tuple(int(x) for x in (re.findall(r"\d+", VERSION) + ["0"] * 4)[:4])
ICON = os.path.join(SPECPATH, "denoiser.ico")


def version_info(description):
    if sys.platform != "win32":
        return None
    from PyInstaller.utils.win32.versioninfo import (
        FixedFileInfo, StringFileInfo, StringStruct, StringTable, VarFileInfo, VarStruct, VSVersionInfo,
    )

    return VSVersionInfo(
        ffi=FixedFileInfo(filevers=_numbers, prodvers=_numbers),
        kids=[
            StringFileInfo([StringTable("041004B0", [
                StringStruct("FileDescription", description),
                StringStruct("FileVersion", VERSION),
                StringStruct("ProductName", "PhotoFlow Studio"),
                StringStruct("ProductVersion", VERSION),
                StringStruct("LegalCopyright", "Copyright (c) 2026 foyer-labs - licenza MIT"),
            ])]),
            VarFileInfo([VarStruct("Translation", [0x0410, 1200])]),
        ],
    )


engine = analysis(os.path.join(SPECPATH, "engine_main.py"))
cli = analysis(os.path.join(SPECPATH, "denoiser_cli.py"))

engine_exe = EXE(PYZ(engine.pure), engine.scripts, [], exclude_binaries=True, name="photoflow-engine",
                 console=True, upx=False, icon=ICON,
                 version=version_info("PhotoFlow Studio - motore di sviluppo e riduzione del rumore"))
cli_exe = EXE(PYZ(cli.pure), cli.scripts, [], exclude_binaries=True, name="denoiser-cli", console=True,
              upx=False, icon=ICON, version=version_info("PhotoFlow Studio - riga di comando di Denoiser"))
coll = COLLECT(engine_exe, engine.binaries, engine.datas, cli_exe, cli.binaries, cli.datas, name="engine", upx=False)
