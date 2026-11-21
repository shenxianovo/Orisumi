CREATE TABLE source_records (
 observation_id INTEGER PRIMARY KEY,run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
 source_id TEXT NOT NULL,source_path TEXT NOT NULL,source_line INTEGER NOT NULL,record_hash TEXT NOT NULL,
 hash_occurrence INTEGER NOT NULL,
 UNIQUE(source_id,record_hash,hash_occurrence));
