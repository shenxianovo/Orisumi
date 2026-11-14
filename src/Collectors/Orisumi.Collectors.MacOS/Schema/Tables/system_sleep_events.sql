CREATE TABLE system_sleep_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('sleep', 'wake')),
    PRIMARY KEY (run_id, run_sequence)
);
