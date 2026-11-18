CREATE TABLE camera_device_presence_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    camera_device_id TEXT NOT NULL REFERENCES camera_devices(camera_device_id),
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'appeared', 'disappeared')),
    cmio_object_id INTEGER NOT NULL,
    PRIMARY KEY (run_id, run_sequence)
);
