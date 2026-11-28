CREATE TABLE window_focus_instances (
    focus_id TEXT PRIMARY KEY NOT NULL, run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    process_instance_id TEXT REFERENCES process_instances(process_instance_id),
    -- A top-level HWND is stable during this window's lifetime. Windows can reuse it after destruction.
    hwnd INTEGER NOT NULL
);
