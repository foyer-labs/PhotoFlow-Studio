-- SPDX-License-Identifier: Apache-2.0
-- Schema 7 (phase 2): labels given in the labelling mode (spec §14), the measuring set of the culling metrics.

CREATE TABLE Labels (
    AssetId     INTEGER PRIMARY KEY REFERENCES Assets (Id),
    Label       TEXT    NOT NULL,
    SessionId   TEXT    NOT NULL,
    LabeledAtMs INTEGER NOT NULL
);
