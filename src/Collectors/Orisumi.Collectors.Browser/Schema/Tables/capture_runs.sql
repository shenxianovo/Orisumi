CREATE TABLE capture_runs (
 run_id TEXT PRIMARY KEY NOT NULL,browser TEXT NOT NULL,profile_id TEXT NOT NULL,
 scope TEXT NOT NULL CHECK(scope IN ('bookmarks','active_tabs','downloads')),
 started_at_utc_ms INTEGER NOT NULL,stopped_at_utc_ms INTEGER);
