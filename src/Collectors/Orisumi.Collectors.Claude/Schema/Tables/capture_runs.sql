CREATE TABLE capture_runs (
 run_id INTEGER PRIMARY KEY, started_at_utc_ms INTEGER NOT NULL, stopped_at_utc_ms INTEGER,
 error TEXT, records_read INTEGER NOT NULL DEFAULT 0, bytes_read INTEGER NOT NULL DEFAULT 0);
