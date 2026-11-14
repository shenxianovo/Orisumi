CREATE TABLE process_instances (
    process_instance_id INTEGER PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    pid INTEGER NOT NULL,
    started_at_utc_us INTEGER,
    UNIQUE(run_id, pid, started_at_utc_us)
);
