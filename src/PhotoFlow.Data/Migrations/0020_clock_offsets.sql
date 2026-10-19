-- SPDX-License-Identifier: Apache-2.0
-- Schema 20 (PhotoFlow Studio, M5): clock correction of the camera that took the photo. CapturedAtMs holds the corrected
-- time (the order of the shots of two bodies in one event); CapturedAtMs - ClockOffsetMs is the time written by the
-- camera, which keeps recognizing the photos already imported.

ALTER TABLE Assets ADD COLUMN ClockOffsetMs INTEGER NOT NULL DEFAULT 0;
