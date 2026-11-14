CREATE TABLE capture_channel_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,channel TEXT NOT NULL,available INTEGER NOT NULL,reason TEXT NOT NULL,
    PRIMARY KEY(run_id,run_sequence)
);
