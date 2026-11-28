CREATE TABLE process_instances (
    process_instance_id TEXT PRIMARY KEY NOT NULL, run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    pid INTEGER NOT NULL, created_at_filetime INTEGER, executable_path TEXT, aumid TEXT, app_name TEXT,
    is_elevated INTEGER CHECK (is_elevated IN (0, 1)),
    UNIQUE (run_id, pid, created_at_filetime)
);
