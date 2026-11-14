CREATE TABLE source_records (
 observation_id INTEGER PRIMARY KEY,run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
 source_path TEXT NOT NULL,source_line INTEGER NOT NULL,record_hash TEXT NOT NULL,
 UNIQUE(source_path,source_line,record_hash));
