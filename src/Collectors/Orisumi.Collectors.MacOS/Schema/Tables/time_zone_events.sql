CREATE TABLE time_zone_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    time_zone_id TEXT NOT NULL,
    PRIMARY KEY (run_id, run_sequence)
);
