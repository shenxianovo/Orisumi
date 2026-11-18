CREATE TABLE browser_windows (
    window_key INTEGER PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    window_id INTEGER NOT NULL CHECK(window_id >= 0),
    UNIQUE(run_id, window_id)
);
