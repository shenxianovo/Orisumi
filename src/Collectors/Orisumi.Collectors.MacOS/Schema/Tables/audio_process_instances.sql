CREATE TABLE audio_process_instances (
    audio_process_instance_id TEXT PRIMARY KEY NOT NULL,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    audio_object_id INTEGER NOT NULL,
    pid INTEGER,
    bundle_id TEXT,
    process_instance_id TEXT REFERENCES process_instances(process_instance_id)
);
