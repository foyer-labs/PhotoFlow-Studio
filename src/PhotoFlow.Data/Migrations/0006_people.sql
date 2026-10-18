-- SPDX-License-Identifier: Apache-2.0
-- Schema 6 (phase 2): people (spec §7, §9). Unnamed persons are the clusters ("Persona N"); prototypes are encrypted
-- fingerprints of confirmed faces, kept per year (P12). Faces may carry a suggestion below the match threshold.

CREATE TABLE Persons (
    Id          INTEGER PRIMARY KEY,
    Name        TEXT,
    Ignored     INTEGER NOT NULL DEFAULT 0,
    CoverFaceId INTEGER,
    CreatedAtMs INTEGER NOT NULL
);
CREATE INDEX IX_Persons_Name ON Persons (Name);

CREATE TABLE PersonPrototypes (
    Id                 INTEGER PRIMARY KEY,
    PersonId           INTEGER NOT NULL REFERENCES Persons (Id),
    FaceId             INTEGER NOT NULL,
    Year               INTEGER,
    EncryptedEmbedding BLOB    NOT NULL,
    ModelVersion       TEXT    NOT NULL
);
CREATE INDEX IX_PersonPrototypes_Person ON PersonPrototypes (PersonId);

ALTER TABLE Faces ADD COLUMN SuggestedPersonId INTEGER;
CREATE INDEX IX_Faces_Person ON Faces (PersonId);
