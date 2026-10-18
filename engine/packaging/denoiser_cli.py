"""Punto di ingresso dell'eseguibile a riga di comando."""

import sys

from denoiser.cli import main

if __name__ == "__main__":
    sys.exit(main())
