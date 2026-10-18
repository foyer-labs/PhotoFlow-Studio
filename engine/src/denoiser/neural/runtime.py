"""Esecuzione dei modelli con ONNX Runtime, sulla GPU quando disponibile."""

from __future__ import annotations

import functools
import gc
import threading
from pathlib import Path

import numpy as np

# In ordine di preferenza: NVIDIA CUDA, DirectML (qualsiasi GPU su Windows), Apple, CPU.
_GPU_PROVIDERS = ("CUDAExecutionProvider", "DmlExecutionProvider", "CoreMLExecutionProvider")

_sessions: dict[tuple[str, str], "InferenceSessionWrapper"] = {}
_lock = threading.Lock()

DEVICES = {"auto": "Automatico (GPU se disponibile)", "cpu": "Solo CPU"}


def available_providers() -> list[str]:
    import onnxruntime as ort

    return list(ort.get_available_providers())


@functools.lru_cache(maxsize=1)
def gpu_status() -> tuple[bool, str]:
    """(scheda video utilizzabile?, descrizione per l'utente)."""
    try:
        gpu = [p for p in available_providers() if p in _GPU_PROVIDERS]
    except Exception:
        gpu = []
    names = {"CUDAExecutionProvider": "NVIDIA CUDA", "DmlExecutionProvider": "DirectML",
             "CoreMLExecutionProvider": "Core ML"}
    if gpu:
        return True, "Scheda video: " + ", ".join(names[p] for p in gpu)
    return False, "Scheda video non disponibile: si usa il processore (più lento)"


def describe_devices() -> str:
    return gpu_status()[1]


class InferenceSessionWrapper:
    def __init__(self, path: Path, device: str):
        self._path = path
        self._device = device
        self._run_lock = threading.Lock()
        self._open()

    def _open(self) -> None:
        import onnxruntime as ort

        options = ort.SessionOptions()
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        available = available_providers()
        providers: list = []
        if self._device != "cpu":
            providers = [p for p in _GPU_PROVIDERS if p in available]
        if "DmlExecutionProvider" in providers:
            # Requisiti di DirectML.
            options.enable_mem_pattern = False
            options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        providers.append("CPUExecutionProvider")
        try:
            self.session = ort.InferenceSession(str(self._path), sess_options=options, providers=providers)
        except Exception:
            if providers == ["CPUExecutionProvider"]:
                raise
            # GPU non utilizzabile (driver, memoria…): si ripiega sulla CPU.
            self.session = ort.InferenceSession(
                str(self._path), sess_options=ort.SessionOptions(), providers=["CPUExecutionProvider"]
            )
        self.provider = self.session.get_providers()[0]
        self.on_gpu = self.provider != "CPUExecutionProvider"
        self.input_name = self.session.get_inputs()[0].name

    def close(self) -> None:
        self.session = None  # libera la memoria (anche sulla scheda video)

    def __call__(self, batch: np.ndarray) -> np.ndarray:
        with self._run_lock:  # una sessione GPU alla volta
            if self.session is None:  # chiusa per far posto a un altro modello mentre la si usava
                self._open()
            feed = {self.input_name: batch.astype(np.float32)}
            try:
                return self.session.run(None, feed)[0]
            except Exception:
                if not self.on_gpu:
                    raise
                # Errore sulla GPU (es. memoria esaurita con tasselli grandi): si continua sulla CPU.
                self._device = "cpu"
                self.session = None
                gc.collect()
                self._open()
                return self.session.run(None, feed)[0]


def get_session(path: Path, device: str = "auto") -> InferenceSessionWrapper:
    """Sessione per il modello ``path``. Sulla scheda video ne resta caricata una sola alla volta.

    DirectML non restituisce la memoria della scheda finché la sessione esiste: tenere insieme DRUNet,
    SCUNet e varianti per tasselli diversi può esaurirla, e a quel punto il driver chiude il processo
    senza errori. Prima di caricare un modello si chiudono quindi le altre sessioni sulla GPU.
    """
    key = (str(path), device)
    with _lock:
        if key not in _sessions:
            gpu_wanted = device != "cpu"
            for other in [k for k, w in _sessions.items() if gpu_wanted and k != key and w.on_gpu]:
                old = _sessions.pop(other)
                with old._run_lock:  # aspetta che finisca un eventuale tassello in corso
                    old.close()
            gc.collect()
            _sessions[key] = InferenceSessionWrapper(path, device)
        return _sessions[key]


def release_sessions() -> None:
    """Chiude tutte le sessioni (es. all'uscita dall'app)."""
    with _lock:
        for wrapper in _sessions.values():
            with wrapper._run_lock:
                wrapper.close()
        _sessions.clear()
    gc.collect()
