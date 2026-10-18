-- SPDX-License-Identifier: Apache-2.0
-- Schema 16 (phase 5): synchronization with the changes other programs make to the XMP (spec §4, decision F5-5).
-- After each write PhotoFlow keeps the state of the file that holds the XMP (FileStampJson: path, size, date, hash)
-- and what it contains (SnapshotJson); a different state means another program changed it, and the differences are
-- imported as the user's. The time of the user's last rating/label change decides conflicts (the most recent wins);
-- every conflict and import is logged.

ALTER TABLE XmpSync ADD COLUMN FileStampJson TEXT;
ALTER TABLE XmpSync ADD COLUMN SnapshotJson TEXT;

ALTER TABLE Decisions ADD COLUMN UserUpdatedAtMs INTEGER;

CREATE TABLE XmpSyncLog (
    Id      INTEGER PRIMARY KEY,
    AssetId INTEGER NOT NULL REFERENCES Assets (Id),
    AtMs    INTEGER NOT NULL,
    Field   TEXT    NOT NULL,
    Value   TEXT,
    Winner  TEXT    NOT NULL,
    Note    TEXT
);
CREATE INDEX IX_XmpSyncLog_Asset ON XmpSyncLog (AssetId);
