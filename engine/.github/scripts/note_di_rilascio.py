"""Scrive la descrizione di una release (note.md) a partire dal CHANGELOG.

Uso: python .github/scripts/note_di_rilascio.py VERSIONE [file_di_uscita]
Controlla che la versione coincida con quella del codice e che il CHANGELOG ne abbia la sezione.
Con GITHUB_OUTPUT impostato scrive anche le uscite «version» e «tag» per il workflow.
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REPO = os.environ.get("GITHUB_REPOSITORY", "foyer-labs/Image-Denoiser")
SERVER = os.environ.get("GITHUB_SERVER_URL", "https://github.com")


def changelog_section(version: str) -> str:
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    m = re.search(rf"^## \[{re.escape(version)}\][^\n]*\n(.*?)(?=^## \[|^\[[^\]]+\]: |\Z)", text, re.S | re.M)
    if not m:
        sys.exit(f"Nessuna sezione [{version}] in CHANGELOG.md")
    body = m.group(1).strip()
    # Nelle release GitHub ogni a capo diventa un'interruzione di riga: si uniscono le righe spezzate.
    body = re.sub(r"\n[ ]{2,}(?=\S)", " ", body)
    body = re.sub(r"(?<=[^\n])\n(?=[^\n#\-\s])", " ", body)
    # I titoli del CHANGELOG (###) diventano sottotitoli della sezione «Novità».
    return re.sub(r"^### ", "#### ", body, flags=re.M)


def main() -> None:
    version = sys.argv[1].strip().removeprefix("v")
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("note.md")
    code = re.search(r'__version__ = "([^"]+)"', (ROOT / "src/denoiser/__init__.py").read_text(encoding="utf-8")).group(1)
    if os.environ.get("CONTROLLA_VERSIONE", "1") == "1" and code != version:
        sys.exit(f"La versione richiesta ({version}) non corrisponde a quella del codice ({code})")
    tag = f"v{version}"
    zip_name = f"Denoiser-windows-{version}.zip"
    blob = f"{SERVER}/{REPO}/blob/{tag}"
    raw = f"https://raw.githubusercontent.com/{REPO}/{tag}"
    preview = "-" in version  # es. 1.0.0-rc1
    parts = [
        "**Denoiser** toglie il rumore dalle foto **RAW, JPG, PNG e TIFF** interamente sul tuo computer: "
        "nessun abbonamento, nessun caricamento nel cloud. Interfaccia in italiano per Windows 10 e 11.",
    ]
    if preview:
        parts.append(
            "> [!NOTE]\n> Versione di **prova**, in vista della prossima versione definitiva. Segnalazioni e proposte nelle "
            f"[Issues]({SERVER}/{REPO}/issues)."
        )
    parts += [
        f"![Confronto prima/dopo al 100%]({raw}/docs/screenshots/02-confronto.jpg)",
        "## Novità",
        changelog_section(version),
        "## Installazione",
        f"1. Scarica **`{zip_name}`** qui sotto, in *Assets*.\n"
        "2. Estrailo dove preferisci (es. `C:\\Programmi\\Denoiser` o il Desktop). Per aggiornare una versione "
        "precedente basta sostituire la cartella: impostazioni e preset restano.\n"
        "3. Avvia **`Denoiser\\Denoiser.exe`**. Python, librerie ed ExifTool sono inclusi; i modelli neurali "
        "(facoltativi) si scaricano dall'app al primo uso.\n\n"
        "Al primo avvio Windows SmartScreen può mostrare «App non riconosciuta», perché l'eseguibile non è firmato "
        "digitalmente: scegli *Ulteriori informazioni → Esegui comunque*. Nella stessa cartella c'è anche "
        "`denoiser-cli.exe` per la riga di comando.",
        "## Requisiti",
        "- Windows 10 o 11 a 64 bit, 8 GB di RAM (16 consigliati per foto oltre i 40 MP).\n"
        "- Facoltativa: una scheda video NVIDIA, AMD o Intel (DirectML) per accelerare le reti neurali.",
        "## Verifica del download",
        f"L'impronta SHA-256 dello zip è nel file `{zip_name}.sha256`. Per controllarla in PowerShell:\n\n"
        f"```powershell\nGet-FileHash .\\{zip_name} -Algorithm SHA256\n```",
        "## Documentazione",
        f"- [Guida all'uso]({blob}/docs/guida.md): comandi, scorciatoie e domande frequenti\n"
        f"- [Come funziona]({blob}/docs/come-funziona.md): metodi, misura del rumore, qualità misurata e limiti noti\n"
        f"- [Riga di comando]({blob}/docs/riga-di-comando.md)\n"
        f"- [Tutte le novità]({blob}/CHANGELOG.md) · [Licenze di terze parti]({blob}/THIRD-PARTY-NOTICES.md)",
        f"Codice con [licenza MIT]({blob}/LICENSE).",
    ]
    out.write_text("\n\n".join(parts) + "\n", encoding="utf-8")
    if "GITHUB_OUTPUT" in os.environ:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as f:
            f.write(f"version={version}\ntag={tag}\n")
    print(f"Note di rilascio per {tag} scritte in {out}")


if __name__ == "__main__":
    main()
