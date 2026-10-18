"""Lettura dei pesi PyTorch (.pth) senza installare PyTorch.

I file .pth sono "pickle" Python: aprirli con il pickle standard eseguirebbe qualunque
codice contenuto nel file. Qui si usa un lettore ristretto che riconosce soltanto le
strutture usate per salvare i tensori (dizionari ordinati e tensori) e rifiuta tutto il resto.

Sono supportati entrambi i formati di PyTorch:
- nuovo (dalla 1.6): archivio ZIP con ``data.pkl`` e un file per ogni blocco di dati;
- vecchio: sequenza di pickle seguita dai dati grezzi.
"""

from __future__ import annotations

import io
import pickle
import struct
import zipfile
from collections import OrderedDict
from pathlib import Path
from typing import Any

import numpy as np

_DTYPES = {
    "FloatStorage": np.float32,
    "HalfStorage": np.float16,
    "DoubleStorage": np.float64,
    "LongStorage": np.int64,
    "IntStorage": np.int32,
    "ShortStorage": np.int16,
    "CharStorage": np.int8,
    "ByteStorage": np.uint8,
    "BoolStorage": np.bool_,
}


class CheckpointError(ValueError):
    pass


class _StorageType:
    def __init__(self, name: str):
        if name not in _DTYPES:
            raise CheckpointError(f"Tipo di dati non supportato: {name}")
        self.dtype = np.dtype(_DTYPES[name])


class _Storage:
    """Riferimento a un blocco di dati, letto pigramente."""

    def __init__(self, dtype: np.dtype, key: str, numel: int | None = None):
        self.dtype = dtype
        self.key = key
        self.numel = numel
        self.array: np.ndarray | None = None


class _Tensor:
    def __init__(self, storage: _Storage, offset: int, size: tuple, stride: tuple):
        self.storage, self.offset, self.size, self.stride = storage, offset, tuple(size), tuple(stride)

    def materialize(self) -> np.ndarray:
        data = self.storage.array
        if data is None:
            raise CheckpointError(f"Dati mancanti per il blocco {self.storage.key}")
        if not self.size:
            return np.array(data[self.offset])
        itemsize = data.dtype.itemsize
        view = np.lib.stride_tricks.as_strided(
            data[self.offset :], shape=self.size, strides=tuple(s * itemsize for s in self.stride)
        )
        return np.array(view)


def _rebuild_tensor(storage, offset, size, stride, *_args) -> _Tensor:
    return _Tensor(storage, offset, size, stride)


def _rebuild_parameter(data, *_args):
    return data


class _RestrictedUnpickler(pickle.Unpickler):
    def __init__(self, file, storages: dict[str, _Storage]):
        super().__init__(file)
        self._storages = storages

    def find_class(self, module: str, name: str) -> Any:
        if module == "collections" and name == "OrderedDict":
            return OrderedDict
        if module == "torch._utils" and name == "_rebuild_tensor_v2":
            return _rebuild_tensor
        if module == "torch._utils" and name == "_rebuild_parameter":
            return _rebuild_parameter
        if module == "torch" and name.endswith("Storage"):
            return _StorageType(name)
        raise CheckpointError(f"Contenuto non ammesso nel file dei pesi: {module}.{name}")

    def persistent_load(self, pid: Any) -> _Storage:
        # Nuovo formato: ('storage', tipo, chiave, posizione, numel)
        # Vecchio formato: ('storage', tipo, chiave, posizione, numel, view_metadata)
        if not isinstance(pid, tuple) or not pid or pid[0] != "storage":
            raise CheckpointError("Riferimento ai dati non riconosciuto")
        storage_type, key = pid[1], str(pid[2])
        numel = int(pid[4]) if len(pid) > 4 else None
        if key not in self._storages:
            self._storages[key] = _Storage(storage_type.dtype, key, numel)
        return self._storages[key]


def _materialize(obj: Any) -> Any:
    if isinstance(obj, _Tensor):
        return obj.materialize()
    if isinstance(obj, dict):
        return OrderedDict((k, _materialize(v)) for k, v in obj.items())
    if isinstance(obj, (list, tuple)):
        return type(obj)(_materialize(v) for v in obj)
    return obj


def _load_zip(path: Path) -> Any:
    storages: dict[str, _Storage] = {}
    with zipfile.ZipFile(path) as zf:
        pkl = next((n for n in zf.namelist() if n.endswith("/data.pkl") or n == "data.pkl"), None)
        if pkl is None:
            raise CheckpointError("Archivio senza data.pkl")
        prefix = pkl[: -len("data.pkl")]
        result = _RestrictedUnpickler(io.BytesIO(zf.read(pkl)), storages).load()
        for key, storage in storages.items():
            raw = zf.read(f"{prefix}data/{key}")
            storage.array = np.frombuffer(raw, dtype=storage.dtype.newbyteorder("<"))
    return result


def _load_legacy(path: Path) -> Any:
    storages: dict[str, _Storage] = {}
    with open(path, "rb") as f:
        safe = _RestrictedUnpickler  # gli header sono tipi semplici: anche qui il lettore ristretto
        magic = safe(f, {}).load()
        if magic != 0x1950A86A20F9469CFC6C:
            raise CheckpointError("Il file non è un file di pesi PyTorch")
        safe(f, {}).load()  # versione del protocollo
        sys_info = safe(f, {}).load()
        if isinstance(sys_info, dict) and not sys_info.get("little_endian", True):
            raise CheckpointError("Formato big-endian non supportato")
        result = safe(f, storages).load()
        keys = safe(f, {}).load()
        for key in keys:
            storage = storages[str(key)]
            (numel,) = struct.unpack("<q", f.read(8))
            nbytes = numel * storage.dtype.itemsize
            storage.array = np.frombuffer(f.read(nbytes), dtype=storage.dtype.newbyteorder("<"))
    return result


def load_state_dict(path: str | Path) -> "OrderedDict[str, np.ndarray]":
    """Restituisce i pesi come dizionario nome -> array numpy."""
    path = Path(path)
    result = _load_zip(path) if zipfile.is_zipfile(path) else _load_legacy(path)
    result = _materialize(result)
    for wrapper in ("params", "state_dict", "model"):
        if isinstance(result, dict) and wrapper in result and isinstance(result[wrapper], dict):
            result = result[wrapper]
    if not isinstance(result, dict):
        raise CheckpointError("Il file non contiene un dizionario di pesi")
    return OrderedDict((k, v) for k, v in result.items() if isinstance(v, np.ndarray))

