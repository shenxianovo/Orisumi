CREATE TABLE samples (
    sample_id INTEGER PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    started_at_utc_ms INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    elapsed_ms INTEGER NOT NULL,
    continuous INTEGER NOT NULL CHECK(continuous IN (0,1)),
    error TEXT,
    UNIQUE(run_id, run_sequence)
);
