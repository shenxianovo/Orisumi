CREATE TABLE capture_runs (
    run_id TEXT NOT NULL PRIMARY KEY,
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER,
    afk_threshold_ms INTEGER CHECK (afk_threshold_ms > 0),
    capture_windows INTEGER NOT NULL, capture_input INTEGER NOT NULL
);
