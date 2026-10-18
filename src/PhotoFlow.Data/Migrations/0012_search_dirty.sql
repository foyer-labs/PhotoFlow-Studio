-- SPDX-License-Identifier: Apache-2.0
-- Schema 12 (phase 3): the photos whose searchable text changed since the last search (tags, labels, blacklist,
-- files, faces, people names), marked by triggers so that the full-text index is updated photo by photo instead of
-- being rebuilt (decision F3-4). The triggers never conflict (NOT EXISTS / NOT IN): inside a trigger SQLite
-- applies the conflict policy of the outer statement, so INSERT OR IGNORE would not be enough under an upsert. The index is built in full once, at the first search after this migration.

CREATE TABLE SearchDirty (
    AssetId INTEGER PRIMARY KEY
);

DELETE FROM SearchState;

CREATE TRIGGER SearchDirty_AssetTags_Insert AFTER INSERT ON AssetTags
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_AssetTags_Update AFTER UPDATE OF State, TagId ON AssetTags
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_AssetTags_Delete AFTER DELETE ON AssetTags
BEGIN INSERT INTO SearchDirty (AssetId) SELECT OLD.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = OLD.AssetId); END;

CREATE TRIGGER SearchDirty_TagLabels_Insert AFTER INSERT ON TagLabels
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM AssetTags WHERE TagId = NEW.TagId AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
CREATE TRIGGER SearchDirty_TagLabels_Update AFTER UPDATE ON TagLabels
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM AssetTags WHERE TagId = NEW.TagId AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
CREATE TRIGGER SearchDirty_TagLabels_Delete AFTER DELETE ON TagLabels
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM AssetTags WHERE TagId = OLD.TagId AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
CREATE TRIGGER SearchDirty_Tags_Blacklist AFTER UPDATE OF Blacklisted ON Tags
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM AssetTags WHERE TagId = NEW.Id AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;

CREATE TRIGGER SearchDirty_Locations_Insert AFTER INSERT ON AssetLocations
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_Locations_Update AFTER UPDATE OF RelativePath, AssetId ON AssetLocations
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_Locations_Delete AFTER DELETE ON AssetLocations
BEGIN INSERT INTO SearchDirty (AssetId) SELECT OLD.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = OLD.AssetId); END;

CREATE TRIGGER SearchDirty_Assets_Insert AFTER INSERT ON Assets
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.Id WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.Id); END;
CREATE TRIGGER SearchDirty_Assets_State AFTER UPDATE OF State ON Assets
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.Id WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.Id); END;
CREATE TRIGGER SearchDirty_Pairs_Insert AFTER INSERT ON AssetPairs
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.RawAssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.RawAssetId); END;

CREATE TRIGGER SearchDirty_Faces_Insert AFTER INSERT ON Faces
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_Faces_Person AFTER UPDATE OF PersonId ON Faces
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_Faces_Delete AFTER DELETE ON Faces
BEGIN INSERT INTO SearchDirty (AssetId) SELECT OLD.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = OLD.AssetId); END;

CREATE TRIGGER SearchDirty_Persons_Update AFTER UPDATE OF Name, Ignored ON Persons
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM Faces WHERE PersonId = NEW.Id AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
