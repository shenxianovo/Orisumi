CREATE TABLE thread_title_observations (
 observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES threads(thread_id),
 run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), title TEXT NOT NULL);
