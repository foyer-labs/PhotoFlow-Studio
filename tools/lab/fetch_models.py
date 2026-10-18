# SPDX-License-Identifier: Apache-2.0
"""Download the models listed in models/manifest.json into PHOTOFLOW_TESTDATA/models, verifying SHA-256.

Usage:
    uv run python fetch_models.py                      # all permissive models
    uv run python fetch_models.py dfine-s-obj2coco     # selected models
    uv run python fetch_models.py --accept-license insightface-buffalo-l insightface-buffalo-l

Models whose license requires acceptance (e.g. buffalo_l, non-commercial research only) are
downloaded only when their id is passed to --accept-license (CLAUDE.md §6).
"""

from __future__ import annotations

import argparse
import sys
import zipfile

import labcommon as lc


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("models", nargs="*", help="model ids (default: all)")
    parser.add_argument("--accept-license", action="append", default=[], metavar="MODEL_ID",
                        help="accept the license of a model that requires explicit acceptance")
    args = parser.parse_args()

    manifest = lc.load_manifest()
    selected = [m for m in manifest["models"] if not args.models or m["id"] in args.models]
    unknown = set(args.models) - {m["id"] for m in manifest["models"]}
    if unknown:
        sys.exit(f"Unknown model ids: {', '.join(sorted(unknown))}")

    for model in selected:
        license_info = model["license"]
        print(f"{model['id']}  [{license_info['spdx']}]  {license_info['source']}")
        if license_info["requiresAcceptance"] and model["id"] not in args.accept_license:
            print(f"  skipped: license requires explicit acceptance (--accept-license {model['id']})")
            if license_info.get("notes"):
                print(f"  {license_info['notes']}")
            continue
        for item in lc.manifest_files(model):
            target = lc.model_dir(model["id"]) / item.path
            lc.download(item.url, target, item.sha256, item.size)
            if item.archive == "zip":
                with zipfile.ZipFile(target) as archive:
                    for member in archive.namelist():
                        if member.endswith("/"):
                            continue
                        name = member.rsplit("/", 1)[-1]
                        destination = lc.model_dir(model["id"]) / name
                        if not destination.exists():
                            destination.write_bytes(archive.read(member))
                print(f"  extracted {target.name}")


if __name__ == "__main__":
    main()
