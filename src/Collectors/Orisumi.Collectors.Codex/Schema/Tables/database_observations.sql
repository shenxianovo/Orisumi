CREATE TABLE database_observations (
    observation_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER NOT NULL
);
