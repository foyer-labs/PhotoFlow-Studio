"""Punto di ingresso del motore a richieste usato da PhotoFlow Studio."""

import sys

from denoiser.server import main

if __name__ == "__main__":
    sys.exit(main())
