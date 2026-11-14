CREATE TABLE capture_method_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id), run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL, channel TEXT NOT NULL,
    mode TEXT NOT NULL CHECK(mode IN ('notification','snapshot')),
    PRIMARY KEY(run_id,run_sequence)
);
