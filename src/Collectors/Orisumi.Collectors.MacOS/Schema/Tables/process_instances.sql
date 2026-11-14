CREATE TABLE process_instances (
    process_instance_id TEXT PRIMARY KEY NOT NULL, run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    pid INTEGER NOT NULL,bundle_id TEXT,app_name TEXT,app_launched_at_utc_ms INTEGER
);
