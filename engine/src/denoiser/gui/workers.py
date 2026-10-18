"""Esecuzione delle elaborazioni in background, con avanzamento e annullamento."""

from __future__ import annotations

import threading
import time
import traceback
from typing import Any, Callable

from PySide6.QtCore import QObject, QRunnable, QThreadPool, Signal


class Cancelled(Exception):
    """Sollevata dalla funzione di avanzamento quando l'utente annulla."""


class TaskSignals(QObject):
    finished = Signal(object)
    failed = Signal(str, str)  # messaggio per l'utente, dettagli tecnici
    cancelled = Signal()
    progress = Signal(float, str)


class Task(QRunnable):
    """Esegue ``fn(*args, **kwargs)`` in un thread.

    Con ``with_progress`` passa anche ``progress=``: una funzione che inoltra l'avanzamento
    all'interfaccia e, se è stato chiesto l'annullamento, interrompe il lavoro.
    """

    def __init__(self, fn: Callable[..., Any], *args: Any, with_progress: bool = False, **kwargs: Any):
        super().__init__()
        self.setAutoDelete(False)
        self.signals = TaskSignals()
        self._fn = fn
        self._args = args
        self._kwargs = kwargs
        self._cancel = threading.Event()
        self.started_at = 0.0
        if with_progress:
            self._kwargs["progress"] = self._progress

    def cancel(self) -> None:
        self._cancel.set()

    @property
    def is_cancelled(self) -> bool:
        return self._cancel.is_set()

    def _progress(self, fraction: float, message: str) -> None:
        if self._cancel.is_set():
            raise Cancelled()
        self._emit(self.signals.progress, float(fraction), message)

    def _emit(self, signal, *args) -> None:
        # Chiudendo l'app mentre un lavoro è in corso gli oggetti Qt possono essere già distrutti:
        # il risultato non serve più a nessuno, quindi lo si scarta in silenzio.
        try:
            signal.emit(*args)
        except RuntimeError:
            self._cancel.set()

    def run(self) -> None:
        self.started_at = time.perf_counter()
        try:
            if self._cancel.is_set():
                raise Cancelled()
            # Dopo la fine del lavoro un annullamento non conta più: il risultato (es. un file salvato)
            # esiste già e va comunicato com'è.
            result = self._fn(*self._args, **self._kwargs)
        except Cancelled:
            self._emit(self.signals.cancelled)
        except Exception as exc:  # mostrato all'utente
            detail = traceback.format_exc()
            self._emit(self.signals.failed, str(exc) or exc.__class__.__name__, detail)
        else:
            self._emit(self.signals.finished, result)
        finally:
            # Rilascia subito foto e array: il Task può restare referenziato più a lungo del lavoro.
            self._fn = None
            self._args = ()
            self._kwargs = {}


def start(task: Task) -> Task:
    """Avvia il lavoro. Si passa la funzione e non il QRunnable: così la proprietà dell'oggetto Python
    resta a Python e la memoria viene liberata (con un QRunnable PySide la cede a C++ per sempre).
    Chi avvia deve tenere un riferimento al Task finché non arriva un suo segnale finale."""
    QThreadPool.globalInstance().start(task.run)
    return task


class EtaEstimator:
    """Stima del tempo rimanente a partire dalla frazione completata."""

    def __init__(self) -> None:
        self.reset()

    def reset(self) -> None:
        self._start = time.perf_counter()

    def text(self, fraction: float) -> str:
        elapsed = time.perf_counter() - self._start
        if fraction <= 0.02 or elapsed < 2.0:
            return ""
        remaining = elapsed * (1.0 - fraction) / fraction
        return "circa " + format_duration(remaining) + " rimanenti"


def format_duration(seconds: float) -> str:
    seconds = max(0, int(round(seconds)))
    if seconds < 60:
        return f"{seconds} s"
    minutes, sec = divmod(seconds, 60)
    if minutes < 60:
        return f"{minutes} min {sec:02d} s" if minutes < 10 else f"{minutes} min"
    hours, minutes = divmod(minutes, 60)
    return f"{hours} h {minutes:02d} min"
