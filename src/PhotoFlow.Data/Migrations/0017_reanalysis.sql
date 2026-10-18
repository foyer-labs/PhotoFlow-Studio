-- SPDX-License-Identifier: Apache-2.0
-- Schema 17 (phase 5): the results of a regeneration wait here until the user confirms them (spec §5: "risultati
-- precedenti conservati fino a conferma", "anteprima delle differenze"); one row per photo and part (culling,
-- embedding, faces, tags, captions, OCR), as JSON. Applied or discarded as a whole, then removed.

CREATE TABLE ReanalysisStaging (
    JobId       INTEGER NOT NULL REFERENCES Jobs (Id),
    AssetId     INTEGER NOT NULL REFERENCES Assets (Id),
    Part        TEXT    NOT NULL,
    PayloadJson TEXT    NOT NULL,
    PRIMARY KEY (JobId, AssetId, Part)
);
