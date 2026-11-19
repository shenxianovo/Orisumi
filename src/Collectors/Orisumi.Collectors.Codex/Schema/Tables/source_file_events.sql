CREATE TABLE source_file_events (
    event_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    rollout_id INTEGER NOT NULL REFERENCES rollouts(rollout_id),
    source_path TEXT NOT NULL,
    file_identity TEXT NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    kind TEXT NOT NULL,
    previous_offset INTEGER,
    observed_length INTEGER
);
