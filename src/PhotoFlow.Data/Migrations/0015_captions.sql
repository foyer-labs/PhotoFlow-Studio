-- SPDX-License-Identifier: Apache-2.0
-- Schema 15 (phase 5): captions (spec §8, levels C/D), one per photo and language, automatic (with the model version)
-- or written by the user (never replaced by an analysis, I5; an empty caption of the user stays empty). They are
-- searchable: the photos whose caption changes are marked in SearchDirty (same form as migration 0012).

CREATE TABLE Captions (
    AssetId      INTEGER NOT NULL REFERENCES Assets (Id),
    Language     TEXT    NOT NULL,
    Text         TEXT    NOT NULL,
    ModelVersion TEXT,
    Origin       TEXT    NOT NULL,
    UpdatedAtMs  INTEGER NOT NULL,
    PRIMARY KEY (AssetId, Language)
);

CREATE TRIGGER SearchDirty_Captions_Insert AFTER INSERT ON Captions
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_Captions_Update AFTER UPDATE OF Text ON Captions
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_Captions_Delete AFTER DELETE ON Captions
BEGIN INSERT INTO SearchDirty (AssetId) SELECT OLD.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = OLD.AssetId); END;
