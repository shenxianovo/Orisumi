CREATE TABLE message_content_observations (
 observation_id INTEGER PRIMARY KEY, thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,message_id TEXT NOT NULL,
 run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),item_json TEXT NOT NULL,
 FOREIGN KEY(thread_id,turn_id,message_id) REFERENCES messages(thread_id,turn_id,message_id));
