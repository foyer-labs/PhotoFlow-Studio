-- SPDX-License-Identifier: Apache-2.0
-- PhotoFlow catalog, schema 1 (phase 1). Spec §9. Forward-only.
-- Enumerations are stored as their names (TEXT), instants as Unix milliseconds (INTEGER, UTC),
-- local capture times keep their offset in a separate column.
-- The SchemaVersion table is managed by the migrator (CatalogMigrator).

CREATE TABLE Libraries (
    Id          INTEGER PRIMARY KEY,
    Name        TEXT    NOT NULL,
    RootPath    TEXT    NOT NULL,
    XmpPolicy   TEXT    NOT NULL,
    IsReadOnly  INTEGER NOT NULL DEFAULT 0,
    Origin      TEXT    NOT NULL,
    LastScanMs  INTEGER
);

CREATE TABLE Assets (
    Id                   INTEGER PRIMARY KEY,
    QuickHash            TEXT    NOT NULL,
    FullHash             TEXT,
    Format               TEXT    NOT NULL,
    Handler              TEXT    NOT NULL,
    Width                INTEGER,
    Height               INTEGER,
    CapturedAtMs         INTEGER,
    CapturedOffsetMin    INTEGER,
    CaptureDateSource    TEXT    NOT NULL,
    CameraModel          TEXT,
    Lens                 TEXT,
    Iso                  INTEGER,
    ExposureTimeSeconds  REAL,
    Aperture             REAL,
    FocalLengthMm        REAL,
    ExposureCompensation REAL,
    AfPoint              TEXT,
    State                TEXT    NOT NULL,
    CreatedAtMs          INTEGER NOT NULL
);
CREATE INDEX IX_Assets_Identity ON Assets (QuickHash, CapturedAtMs);
CREATE INDEX IX_Assets_FullHash ON Assets (FullHash);

CREATE TABLE AssetLocations (
    Id           INTEGER PRIMARY KEY,
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    LibraryId    INTEGER NOT NULL REFERENCES Libraries (Id),
    RelativePath TEXT    NOT NULL,
    LastSeenMs   INTEGER NOT NULL,
    UNIQUE (LibraryId, RelativePath)
);
CREATE INDEX IX_AssetLocations_Asset ON AssetLocations (AssetId);

CREATE TABLE AssetPairs (
    Id          INTEGER PRIMARY KEY,
    RawAssetId  INTEGER NOT NULL REFERENCES Assets (Id),
    JpegAssetId INTEGER NOT NULL REFERENCES Assets (Id),
    UNIQUE (RawAssetId, JpegAssetId)
);

CREATE TABLE Jobs (
    Id                INTEGER PRIMARY KEY,
    Type              TEXT    NOT NULL,
    Source            TEXT,
    EventName         TEXT,
    EventDate         TEXT,
    ProfileId         TEXT,
    AutomaticProfile  INTEGER NOT NULL DEFAULT 0,
    FallbackProfileId TEXT,
    State             TEXT    NOT NULL,
    Priority          TEXT    NOT NULL,
    SettingsJson      TEXT    NOT NULL,
    CreatedAtMs       INTEGER NOT NULL,
    UpdatedAtMs       INTEGER NOT NULL
);

CREATE TABLE JobItems (
    Id                 INTEGER PRIMARY KEY,
    JobId              INTEGER NOT NULL REFERENCES Jobs (Id),
    AssetId            INTEGER REFERENCES Assets (Id),
    SourcePath         TEXT    NOT NULL,
    TargetRelativePath TEXT,
    ContentHash        TEXT,
    LastCompletedStage TEXT    NOT NULL,
    State              TEXT    NOT NULL,
    Error              TEXT,
    UpdatedAtMs        INTEGER NOT NULL,
    UNIQUE (JobId, SourcePath)
);

CREATE TABLE Models (
    Id       INTEGER PRIMARY KEY,
    Name     TEXT NOT NULL,
    Version  TEXT NOT NULL,
    Sha256   TEXT NOT NULL,
    License  TEXT NOT NULL,
    Function TEXT NOT NULL,
    Status   TEXT NOT NULL,
    UNIQUE (Name, Version)
);

CREATE TABLE AnalyzerResults (
    Id           INTEGER PRIMARY KEY,
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    AnalyzerId   TEXT    NOT NULL,
    ModelVersion TEXT    NOT NULL,
    ProfileId    TEXT    NOT NULL,
    ScoresJson   TEXT    NOT NULL,
    CreatedAtMs  INTEGER NOT NULL
);
CREATE INDEX IX_AnalyzerResults_Asset ON AnalyzerResults (AssetId, AnalyzerId);

CREATE TABLE Faces (
    Id                 INTEGER PRIMARY KEY,
    AssetId            INTEGER NOT NULL REFERENCES Assets (Id),
    X1 REAL NOT NULL, Y1 REAL NOT NULL, X2 REAL NOT NULL, Y2 REAL NOT NULL,
    Landmarks          BLOB,
    Confidence         REAL    NOT NULL,
    LeftEye            TEXT    NOT NULL,
    RightEye           TEXT    NOT NULL,
    EncryptedEmbedding BLOB,
    PersonId           INTEGER,
    AssignmentOrigin   TEXT,
    Similarity         REAL,
    ModelVersion       TEXT    NOT NULL
);
CREATE INDEX IX_Faces_Asset ON Faces (AssetId);

CREATE TABLE Decisions (
    Id               INTEGER PRIMARY KEY,
    AssetId          INTEGER NOT NULL UNIQUE REFERENCES Assets (Id),
    AutomaticOutcome TEXT,
    ReasonsJson      TEXT    NOT NULL DEFAULT '[]',
    ModelVersion     TEXT,
    UserOutcome      TEXT,
    Rating           INTEGER,
    Label            TEXT,
    UpdatedAtMs      INTEGER NOT NULL
);

CREATE TABLE Corrections (
    Id               INTEGER PRIMARY KEY,
    AssetId          INTEGER NOT NULL REFERENCES Assets (Id),
    AutomaticOutcome TEXT,
    UserOutcome      TEXT    NOT NULL,
    ProfileId        TEXT    NOT NULL,
    SignalsJson      TEXT    NOT NULL,
    CorrectedAtMs    INTEGER NOT NULL
);

CREATE TABLE XmpSync (
    AssetId       INTEGER PRIMARY KEY REFERENCES Assets (Id),
    LastWrittenMs INTEGER,
    XmpSignature  TEXT,
    WritePending  INTEGER NOT NULL DEFAULT 0,
    LastError     TEXT
);

CREATE TABLE UndoLog (
    Id          INTEGER PRIMARY KEY,
    AtMs        INTEGER NOT NULL,
    Operation   TEXT    NOT NULL,
    PayloadJson TEXT    NOT NULL
);
