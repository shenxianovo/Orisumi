CREATE TABLE capture_channel_events (
 event_id TEXT PRIMARY KEY NOT NULL, browser TEXT NOT NULL, profile_id TEXT NOT NULL,
 scope TEXT NOT NULL, mode TEXT NOT NULL CHECK(mode IN ('notification','replay','snapshot')),
 started_at_utc_ms INTEGER NOT NULL, stopped_at_utc_ms INTEGER,
 evidence TEXT NOT NULL, detail TEXT, run_id TEXT,
 CHECK(stopped_at_utc_ms IS NULL OR stopped_at_utc_ms >= started_at_utc_ms));
