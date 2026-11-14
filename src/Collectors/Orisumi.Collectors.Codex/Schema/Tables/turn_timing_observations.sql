CREATE TABLE turn_timing_observations (observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,
 run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),started_at_utc_ms INTEGER, completed_at_utc_ms INTEGER, FOREIGN KEY(thread_id,turn_id) REFERENCES turns(thread_id,turn_id));
