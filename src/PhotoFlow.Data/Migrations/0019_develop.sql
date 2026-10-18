-- SPDX-License-Identifier: Apache-2.0
-- Schema 19 (PhotoFlow Studio, decision S9): development settings of each photo (JSON of the engine's settings:
-- white balance, tones, curve, noise reduction) and the exports (what has been delivered, where, with which recipe).

CREATE TABLE DevelopSettings (
    AssetId      INTEGER PRIMARY KEY REFERENCES Assets (Id),
    SettingsJson TEXT    NOT NULL,
    UpdatedAtMs  INTEGER NOT NULL
);

CREATE TABLE Exports (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    OutputPath   TEXT    NOT NULL,
    Recipe       TEXT    NOT NULL,
    ExportedAtMs INTEGER NOT NULL
);

CREATE INDEX Exports_Asset ON Exports (AssetId, ExportedAtMs);
