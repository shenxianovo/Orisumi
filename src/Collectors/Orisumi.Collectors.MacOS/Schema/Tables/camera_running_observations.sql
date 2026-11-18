CREATE TABLE camera_running_observations (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    camera_device_id TEXT NOT NULL REFERENCES camera_devices(camera_device_id),
    is_running INTEGER CHECK (is_running IN (0, 1)),
    PRIMARY KEY (run_id, run_sequence)
);
