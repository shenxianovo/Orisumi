CREATE TABLE capture_channel_events (
    event_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    scope TEXT NOT NULL,
    mode TEXT NOT NULL CHECK(mode IN ('notification','replay','snapshot')),
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER,
    evidence TEXT NOT NULL,
    detail TEXT,
    CHECK(stopped_at_utc_ms IS NULL OR stopped_at_utc_ms >= started_at_utc_ms)
);
