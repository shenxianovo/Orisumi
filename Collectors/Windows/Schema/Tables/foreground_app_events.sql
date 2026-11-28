CREATE TABLE foreground_app_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'activated')),
    has_app INTEGER CHECK (has_app IN (0, 1)),
    process_instance_id TEXT REFERENCES process_instances(process_instance_id),
    CHECK (has_app IS 1 OR process_instance_id IS NULL),
    PRIMARY KEY (run_id, run_sequence)
);
