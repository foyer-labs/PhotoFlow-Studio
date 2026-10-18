-- SPDX-License-Identifier: Apache-2.0
-- Schema 8 (phase 3): tags with labels per language, their assignments with source, confidence, model version and
-- state (spec §8, §9), and the personal CLIP tags. One row per photo and tag: a manual assignment replaces an
-- automatic one; a tag removed by the user stays as "Suppressed" (I5).

CREATE TABLE Tags (
    Id          INTEGER PRIMARY KEY,
    Key         TEXT    NOT NULL UNIQUE,
    Category    TEXT,
    Source      TEXT    NOT NULL,
    Reviewed    INTEGER NOT NULL DEFAULT 0,
    Blacklisted INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE TagLabels (
    TagId    INTEGER NOT NULL REFERENCES Tags (Id),
    Language TEXT    NOT NULL,
    Label    TEXT    NOT NULL,
    PRIMARY KEY (TagId, Language)
);

CREATE TABLE AssetTags (
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    TagId        INTEGER NOT NULL REFERENCES Tags (Id),
    Source       TEXT    NOT NULL,
    Confidence   REAL,
    ModelVersion TEXT,
    State        TEXT    NOT NULL,
    UpdatedAtMs  INTEGER NOT NULL,
    PRIMARY KEY (AssetId, TagId)
);

CREATE INDEX IX_AssetTags_Tag ON AssetTags (TagId, State);

CREATE TABLE CustomTags (
    TagId           INTEGER PRIMARY KEY REFERENCES Tags (Id),
    ClipDescription TEXT    NOT NULL,
    Threshold       REAL    NOT NULL,
    Margin          REAL    NOT NULL
);
