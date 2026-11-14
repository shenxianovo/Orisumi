CREATE TABLE foreground_app_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('activated', 'snapshot')),
    process_instance_id TEXT REFERENCES process_instances(process_instance_id),
    PRIMARY KEY (run_id, run_sequence)
);
