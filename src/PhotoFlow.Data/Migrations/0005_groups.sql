-- SPDX-License-Identifier: Apache-2.0
-- Schema 5 (phase 2): groups of a job (bursts, bracketing, timelapse, panorama) and their members (spec §6, §9).
-- Automatic groups are recomputed when the job's batch stages run; manual groups are never touched by them.

CREATE TABLE Groups (
    Id                INTEGER PRIMARY KEY,
    JobId             INTEGER NOT NULL REFERENCES Jobs (Id),
    Type              TEXT    NOT NULL,
    ProfileId         TEXT,
    ProfileConfidence REAL,
    Origin            TEXT    NOT NULL,
    CreatedAtMs       INTEGER NOT NULL
);
CREATE INDEX IX_Groups_Job ON Groups (JobId);

CREATE TABLE GroupMembers (
    GroupId  INTEGER NOT NULL REFERENCES Groups (Id),
    AssetId  INTEGER NOT NULL REFERENCES Assets (Id),
    Position INTEGER NOT NULL,
    Score    REAL,
    PRIMARY KEY (GroupId, AssetId)
);
CREATE INDEX IX_GroupMembers_Asset ON GroupMembers (AssetId);

-- Profile of each analyzed photo of a job (D8): the job profile unless CLIP assigned another one (automatic mode,
-- fallback under the confidence threshold) or the user chose one for its group ("re-analyze as…", never overwritten
-- by the automatic assignment). In manual mode only the suggestion is stored.
CREATE TABLE PhotoProfiles (
    JobId               INTEGER NOT NULL REFERENCES Jobs (Id),
    AssetId             INTEGER NOT NULL REFERENCES Assets (Id),
    ProfileId           TEXT,
    Confidence          REAL,
    ByUser              INTEGER NOT NULL DEFAULT 0,
    SuggestedProfileId  TEXT,
    SuggestedConfidence REAL,
    PRIMARY KEY (JobId, AssetId)
);

-- Face fingerprints (spec §7): the 5 alignment keypoints (normalized, so that a lost key can be recomputed from the
-- preview without detecting again) and the model of the fingerprint. The fingerprint itself is always encrypted.
ALTER TABLE Faces ADD COLUMN Keypoints BLOB;
ALTER TABLE Faces ADD COLUMN EmbeddingModel TEXT;
