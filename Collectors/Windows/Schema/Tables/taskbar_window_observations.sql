CREATE TABLE taskbar_window_observations (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    process_instance_id TEXT NOT NULL REFERENCES process_instances(process_instance_id),
    has_taskbar_window INTEGER CHECK (has_taskbar_window IN (0, 1)),
    PRIMARY KEY (run_id, run_sequence)
);
