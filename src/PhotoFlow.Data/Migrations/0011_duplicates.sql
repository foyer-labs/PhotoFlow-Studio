-- SPDX-License-Identifier: Apache-2.0
-- Schema 11 (phase 3): perceptual hashes of the thumbnails (near duplicates, decision F3-5) and the groups the user
-- marked as "not duplicates" (P5), never proposed again.

CREATE TABLE PerceptualHashes (
    AssetId INTEGER PRIMARY KEY REFERENCES Assets (Id),
    Hash    INTEGER NOT NULL
);

CREATE TABLE DuplicateExclusions (
    Key         TEXT    PRIMARY KEY,
    CreatedAtMs INTEGER NOT NULL
);
