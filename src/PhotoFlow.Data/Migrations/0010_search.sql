-- SPDX-License-Identifier: Apache-2.0
-- Schema 10 (phase 3): full-text index of the photos (file names and folders, tag labels in English and Italian,
-- people names), rebuilt from the catalog when its content changes (decision F3-4); indexes for the exact filters.

CREATE VIRTUAL TABLE SearchText USING fts5 (Content, tokenize = 'unicode61 remove_diacritics 2');

CREATE TABLE SearchState (
    Id          INTEGER PRIMARY KEY CHECK (Id = 1),
    Fingerprint TEXT    NOT NULL
);

CREATE INDEX IX_Assets_Captured ON Assets (CapturedAtMs);
CREATE INDEX IX_AssetTags_Asset ON AssetTags (AssetId, State);
CREATE INDEX IX_AssetPairs_Raw ON AssetPairs (RawAssetId);
