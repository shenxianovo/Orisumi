CREATE TABLE audio_process_presence_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    audio_process_instance_id TEXT NOT NULL REFERENCES audio_process_instances(audio_process_instance_id),
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'appeared', 'disappeared')),
    PRIMARY KEY (run_id, run_sequence)
);
