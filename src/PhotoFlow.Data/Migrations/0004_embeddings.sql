-- SPDX-License-Identifier: Apache-2.0
-- Schema 4 (phase 2): image embeddings (CLIP) per asset and model version, float32 little-endian (spec §9).
-- Bursts, similar photos and zero-shot use them; a new model writes beside the old one until the switch (§5).

CREATE TABLE Embeddings (
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    ModelVersion TEXT    NOT NULL,
    Vector       BLOB    NOT NULL,
    CreatedAtMs  INTEGER NOT NULL,
    PRIMARY KEY (AssetId, ModelVersion)
);
