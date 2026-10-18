"""Interfaccia comune degli algoritmi di denoise.

Ogni algoritmo dichiara i propri parametri con :class:`Param`: l'interfaccia grafica
li usa per costruire automaticamente i controlli, la CLI e i preset per validarli.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any

import numpy as np


@dataclass(frozen=True)
class Param:
    key: str
    label: str
    kind: str  # "float", "int", "choice", "bool"
    default: Any
    minimum: float | None = None
    maximum: float | None = None
    step: float | None = None
    #: Per kind == "choice": valore -> etichetta.
    choices: dict[str, str] | None = None
    tooltip: str = ""

    def coerce(self, value: Any) -> Any:
        """Converte e limita un valore all'intervallo ammesso."""
        if value is None:
            return self.default
        if self.kind == "bool":
            return bool(value)
        if self.kind == "choice":
            value = str(value)
            return value if self.choices and value in self.choices else self.default
        try:
            num = float(str(value).replace(",", ".")) if isinstance(value, str) else float(value)
        except (TypeError, ValueError):
            return self.default  # es. un preset scritto a mano con un valore non numerico
        if num != num:  # NaN
            return self.default
        if self.minimum is not None:
            num = max(num, self.minimum)
        if self.maximum is not None:
            num = min(num, self.maximum)
        return int(round(num)) if self.kind == "int" else num


class Denoiser(ABC):
    key: str = ""
    label: str = ""
    #: Riassunto di poche parole mostrato accanto al nome nel menu dei metodi.
    summary: str = ""
    description: str = ""
    params: tuple[Param, ...] = ()
    #: Pixel di contorno necessari per elaborare un ritaglio senza artefatti ai bordi.
    margin: int = 16
    #: True per gli algoritmi che elaborano i tre canali insieme (reti neurali).
    joint: bool = False
    #: True se l'algoritmo non usa il livello di rumore (la forza diventa una miscela).
    blind: bool = False

    def is_available(self) -> bool:
        """False se mancano risorse da scaricare (es. i pesi di una rete neurale)."""
        return True

    def resolve(self, values: dict[str, Any] | None) -> dict[str, Any]:
        values = values or {}
        return {p.key: p.coerce(values.get(p.key, p.default)) for p in self.params}

    def defaults(self) -> dict[str, Any]:
        return {p.key: p.default for p in self.params}

    @abstractmethod
    def denoise(self, channel: np.ndarray, sigma: float, params: dict[str, Any]) -> np.ndarray:
        """Ripulisce un singolo canale 2D float32 (intervallo di valori qualsiasi).

        ``sigma`` è la deviazione standard del rumore da rimuovere, nelle stesse unità
        del canale (già moltiplicata per la forza scelta dall'utente).
        """


def to_unit_range(channel: np.ndarray) -> tuple[np.ndarray, float, float]:
    """Porta un canale in [0, 1]; restituisce anche i valori per tornare indietro."""
    lo = float(channel.min())
    hi = float(channel.max())
    scale = hi - lo if hi > lo else 1.0
    return (channel - lo) / scale, lo, scale
