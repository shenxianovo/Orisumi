CREATE TABLE capture_read_failures (
    event_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    observed_at_utc_ms INTEGER NOT NULL,
    error TEXT NOT NULL,
    scope TEXT
);
