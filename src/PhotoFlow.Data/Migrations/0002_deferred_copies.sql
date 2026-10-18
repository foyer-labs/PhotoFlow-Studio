-- SPDX-License-Identifier: Apache-2.0
-- Schema 2 (phase 1): queue of asynchronous copies to deferred destinations (NAS), spec §4.

CREATE TABLE DeferredCopies (
    Id                 INTEGER PRIMARY KEY,
    JobId              INTEGER REFERENCES Jobs (Id),
    SourcePath         TEXT    NOT NULL,
    DestinationId      TEXT    NOT NULL,
    DestinationRoot    TEXT    NOT NULL,
    TargetRelativePath TEXT    NOT NULL,
    ContentHash        TEXT    NOT NULL,
    Attempts           INTEGER NOT NULL DEFAULT 0,
    NextAttemptMs      INTEGER NOT NULL,
    State              TEXT    NOT NULL,
    LastError          TEXT,
    UNIQUE (DestinationId, TargetRelativePath)
);
CREATE INDEX IX_DeferredCopies_Due ON DeferredCopies (State, NextAttemptMs);
