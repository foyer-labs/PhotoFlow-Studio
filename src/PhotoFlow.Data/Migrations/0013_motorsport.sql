-- SPDX-License-Identifier: Apache-2.0
-- Schema 13 (phase 4): entry lists (spec §8, §9 EntryLists), the list used by a job, the OCR readings on each
-- vehicle (OcrResults) and the race number of each vehicle with its state (certain, to review, none, out of the
-- list) and origin (automatic, propagated in the burst, manual: never replaced, I5).

CREATE TABLE EntryLists (
    Id          INTEGER PRIMARY KEY,
    Name        TEXT    NOT NULL,
    SourceFile  TEXT,
    CreatedAtMs INTEGER NOT NULL
);

CREATE TABLE Entries (
    Id          INTEGER PRIMARY KEY,
    ListId      INTEGER NOT NULL REFERENCES EntryLists (Id),
    Number      TEXT    NOT NULL,
    DriversJson TEXT    NOT NULL,
    Team        TEXT,
    Category    TEXT,
    Car         TEXT,
    UNIQUE (ListId, Number)
);

CREATE TABLE JobEntryLists (
    JobId  INTEGER PRIMARY KEY REFERENCES Jobs (Id),
    ListId INTEGER NOT NULL REFERENCES EntryLists (Id)
);

CREATE TABLE OcrResults (
    Id           INTEGER PRIMARY KEY,
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    Vehicle      INTEGER NOT NULL,
    VX1 REAL NOT NULL, VY1 REAL NOT NULL, VX2 REAL NOT NULL, VY2 REAL NOT NULL,
    X1 REAL NOT NULL, Y1 REAL NOT NULL, X2 REAL NOT NULL, Y2 REAL NOT NULL,
    Text         TEXT    NOT NULL,
    Confidence   REAL    NOT NULL,
    ModelVersion TEXT    NOT NULL
);
CREATE INDEX IX_OcrResults_Asset ON OcrResults (AssetId);

CREATE TABLE VehicleNumbers (
    Id             INTEGER PRIMARY KEY,
    AssetId        INTEGER NOT NULL REFERENCES Assets (Id),
    Vehicle        INTEGER NOT NULL,
    X1 REAL NOT NULL, Y1 REAL NOT NULL, X2 REAL NOT NULL, Y2 REAL NOT NULL,
    Number         TEXT,
    State          TEXT    NOT NULL,
    Origin         TEXT    NOT NULL,
    CandidatesJson TEXT    NOT NULL,
    UpdatedAtMs    INTEGER NOT NULL,
    UNIQUE (AssetId, Vehicle)
);
CREATE INDEX IX_VehicleNumbers_Number ON VehicleNumbers (Number);
