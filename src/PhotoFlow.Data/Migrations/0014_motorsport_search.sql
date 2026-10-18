-- SPDX-License-Identifier: Apache-2.0
-- Schema 14 (phase 4): the names of the entry list are searchable (spec §8: crews keep only number and team in the
-- XMP, D18, but every driver is found in the catalog). The photos whose certain numbers, job list or list entries
-- change are marked in SearchDirty (same never-conflicting form as migration 0012).

CREATE INDEX IX_JobItems_Asset ON JobItems (AssetId);

CREATE TRIGGER SearchDirty_VehicleNumbers_Insert AFTER INSERT ON VehicleNumbers
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_VehicleNumbers_Update AFTER UPDATE OF Number, State ON VehicleNumbers
BEGIN INSERT INTO SearchDirty (AssetId) SELECT NEW.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = NEW.AssetId); END;
CREATE TRIGGER SearchDirty_VehicleNumbers_Delete AFTER DELETE ON VehicleNumbers
BEGIN INSERT INTO SearchDirty (AssetId) SELECT OLD.AssetId WHERE NOT EXISTS (SELECT 1 FROM SearchDirty WHERE AssetId = OLD.AssetId); END;

CREATE TRIGGER SearchDirty_JobEntryLists_Insert AFTER INSERT ON JobEntryLists
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM JobItems WHERE JobId = NEW.JobId AND AssetId IS NOT NULL AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
CREATE TRIGGER SearchDirty_JobEntryLists_Update AFTER UPDATE OF ListId ON JobEntryLists
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT AssetId FROM JobItems WHERE JobId = NEW.JobId AND AssetId IS NOT NULL AND AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;

CREATE TRIGGER SearchDirty_Entries_Insert AFTER INSERT ON Entries
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT v.AssetId FROM VehicleNumbers v JOIN JobItems ji ON ji.AssetId = v.AssetId JOIN JobEntryLists jl ON jl.JobId = ji.JobId
      WHERE jl.ListId = NEW.ListId AND v.Number = NEW.Number AND v.AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
CREATE TRIGGER SearchDirty_Entries_Update AFTER UPDATE ON Entries
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT v.AssetId FROM VehicleNumbers v JOIN JobItems ji ON ji.AssetId = v.AssetId JOIN JobEntryLists jl ON jl.JobId = ji.JobId
      WHERE jl.ListId = NEW.ListId AND v.Number IN (OLD.Number, NEW.Number) AND v.AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
CREATE TRIGGER SearchDirty_Entries_Delete AFTER DELETE ON Entries
BEGIN INSERT INTO SearchDirty (AssetId) SELECT DISTINCT v.AssetId FROM VehicleNumbers v JOIN JobItems ji ON ji.AssetId = v.AssetId JOIN JobEntryLists jl ON jl.JobId = ji.JobId
      WHERE jl.ListId = OLD.ListId AND v.Number = OLD.Number AND v.AssetId NOT IN (SELECT AssetId FROM SearchDirty); END;
