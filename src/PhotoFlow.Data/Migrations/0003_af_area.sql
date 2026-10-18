-- SPDX-License-Identifier: Apache-2.0
-- Schema 3 (phase 1): AF area of the capture, normalized and oriented like the displayed image ("x1 y1 x2 y2"),
-- first choice of the sharpness zone (spec §6).

ALTER TABLE Assets ADD COLUMN AfArea TEXT;
