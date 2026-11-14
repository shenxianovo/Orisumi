CREATE TABLE database_observations (
    run_id INTEGER PRIMARY KEY REFERENCES capture_runs(run_id),
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER NOT NULL
);
