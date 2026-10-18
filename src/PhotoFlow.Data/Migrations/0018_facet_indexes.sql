-- SPDX-License-Identifier: Apache-2.0
-- Facets of Consultazione (spec §12bis, 13 Oct 2026): the counts per tag and per person over a result read only the
-- index, already in tag (person) order, instead of a row of the table for every tag of every photo (1.7 s → ms at
-- 60,000 photos). The new indexes extend the old ones, which they replace.

DROP INDEX IF EXISTS IX_AssetTags_Tag;
CREATE INDEX IX_AssetTags_Tag ON AssetTags (TagId, State, AssetId);

DROP INDEX IF EXISTS IX_Faces_Person;
CREATE INDEX IX_Faces_Person ON Faces (PersonId, AssetId);
