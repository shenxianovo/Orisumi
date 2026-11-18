CREATE TABLE audio_input_observations (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    audio_process_instance_id TEXT NOT NULL REFERENCES audio_process_instances(audio_process_instance_id),
    is_running INTEGER CHECK (is_running IN (0, 1)),
    PRIMARY KEY (run_id, run_sequence)
);
