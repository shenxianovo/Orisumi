CREATE TABLE usage_source_events (
    event_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    thread_id TEXT NOT NULL REFERENCES threads(thread_id),
    source_path TEXT NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    available INTEGER NOT NULL CHECK(available IN (0,1))
);
