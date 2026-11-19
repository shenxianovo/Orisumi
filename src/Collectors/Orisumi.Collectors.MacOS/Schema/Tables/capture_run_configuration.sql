CREATE TABLE capture_run_configuration (
    run_id INTEGER PRIMARY KEY REFERENCES capture_runs(run_id),
    afk_threshold_ms INTEGER NOT NULL CHECK (afk_threshold_ms > 0),
    capture_windows INTEGER NOT NULL CHECK (capture_windows IN (0, 1)),
    capture_input INTEGER NOT NULL CHECK (capture_input IN (0, 1)),
    capture_microphone INTEGER NOT NULL CHECK (capture_microphone IN (0, 1)),
    capture_audio_output INTEGER NOT NULL CHECK (capture_audio_output IN (0, 1)),
    capture_camera INTEGER NOT NULL CHECK (capture_camera IN (0, 1))
);
