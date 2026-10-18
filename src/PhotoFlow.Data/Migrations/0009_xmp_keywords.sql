-- SPDX-License-Identifier: Apache-2.0
-- Schema 9 (phase 3): the keywords PhotoFlow wrote in the XMP of each photo, so that a tag removed later is taken out
-- of the file without ever touching keywords written by other programs or by the user.

ALTER TABLE XmpSync ADD COLUMN WrittenFlatJson TEXT;
ALTER TABLE XmpSync ADD COLUMN WrittenHierarchicalJson TEXT;
