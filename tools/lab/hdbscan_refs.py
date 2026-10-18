# SPDX-License-Identifier: Apache-2.0
"""References for the C# HDBSCAN (spec §7, R18, decision F2-2): scikit-learn HDBSCAN on normalized vectors with the
cosine distance, both cluster selections ("eom" and "leaf").

Data: the ArcFace fingerprints of the local faces (face_embed_refs.py, never in the repository) and synthetic
clusters on the unit sphere (to exercise noise, uneven sizes and ties).

Output: PHOTOFLOW_TESTDATA/parity/hdbscan/<name>.vectors.bin (float32 [n, d]) and index.json with, per data set,
n, d, min_cluster_size, min_samples, the labels of each selection and the sorted single-linkage heights. With tied
mutual reachability distances (frequent from min_samples 3) numpy's unstable argsort decides the order of equal
merges, so labels may legitimately differ around ties; the heights do not.

Usage:
    uv run python hdbscan_refs.py
"""

from __future__ import annotations

import json

import numpy as np
import sklearn
from sklearn.cluster import HDBSCAN

import labcommon as lc

SETTINGS = [(3, 3), (3, 2), (5, 5)]


def synthetic(seed: int, dims: int = 64) -> np.ndarray:
    rng = np.random.default_rng(seed)
    centers = rng.normal(size=(8, dims))
    sizes = [40, 25, 12, 8, 5, 4, 3, 2]
    points = [c + rng.normal(scale=0.35, size=(s, dims)) for c, s in zip(centers, sizes)]
    points.append(rng.normal(size=(20, dims)) * 2)  # noise
    data = np.concatenate(points).astype(np.float32)
    return data / np.linalg.norm(data, axis=1, keepdims=True)


def main() -> None:
    out = lc.output_dir("parity", "hdbscan")
    faces = lc.output_dir("parity", "faces")
    index_faces = json.loads((faces / "embed_index.json").read_text(encoding="utf-8"))
    arc = np.stack([np.fromfile(faces / f"{f['image']}.f{f['face']}.arc_emb.bin", dtype=np.float32) for f in index_faces["faces"]])
    arc = arc / np.linalg.norm(arc, axis=1, keepdims=True)
    sets = {"faces": arc, "synthetic1": synthetic(1), "synthetic2": synthetic(2)}

    index = {"sklearn": sklearn.__version__, "sets": []}
    for name, data in sets.items():
        data.astype(np.float32).tofile(out / f"{name}.vectors.bin")
        entry = {"name": name, "n": int(data.shape[0]), "d": int(data.shape[1]), "runs": []}
        for min_cluster_size, min_samples in SETTINGS:
            run = {"min_cluster_size": min_cluster_size, "min_samples": min_samples}
            for selection in ("eom", "leaf"):
                model = HDBSCAN(min_cluster_size=min_cluster_size, min_samples=min_samples, metric="cosine",
                                cluster_selection_method=selection, algorithm="brute")
                labels = model.fit_predict(data.astype(np.float64))
                run[selection] = labels.tolist()
                # Merge heights are unique even when ties make the dendrogram topology arbitrary.
                run["heights"] = np.sort(model._single_linkage_tree_["value"]).tolist()
                print(f"{name} mcs={min_cluster_size} ms={min_samples} {selection}: {labels.max() + 1} clusters, "
                      f"{int((labels < 0).sum())} noise")
            entry["runs"].append(run)
        index["sets"].append(entry)
    (out / "index.json").write_text(json.dumps(index), encoding="utf-8")


if __name__ == "__main__":
    main()
