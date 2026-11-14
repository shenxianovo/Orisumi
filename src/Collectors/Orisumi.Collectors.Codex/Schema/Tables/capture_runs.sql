CREATE TABLE capture_runs (
    run_id INTEGER PRIMARY KEY,
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER,
    thread_filter TEXT,
    trigger TEXT NOT NULL,
    error TEXT
);
