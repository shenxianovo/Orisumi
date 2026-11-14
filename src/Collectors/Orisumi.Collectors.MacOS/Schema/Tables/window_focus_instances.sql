CREATE TABLE window_focus_instances (
    focus_id TEXT PRIMARY KEY NOT NULL,run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    process_instance_id TEXT NOT NULL REFERENCES process_instances(process_instance_id)
);
