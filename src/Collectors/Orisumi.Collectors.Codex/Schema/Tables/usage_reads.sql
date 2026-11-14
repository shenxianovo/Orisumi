CREATE TABLE usage_reads (
    read_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    thread_id TEXT NOT NULL REFERENCES threads(thread_id),
    source_path TEXT NOT NULL,
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER NOT NULL,
    start_offset INTEGER NOT NULL,
    end_offset INTEGER NOT NULL,
    bytes_read INTEGER NOT NULL,
    first_line INTEGER NOT NULL,
    last_line INTEGER NOT NULL,
    reset_reason TEXT
);
