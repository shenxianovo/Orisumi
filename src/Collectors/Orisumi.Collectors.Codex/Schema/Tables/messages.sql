CREATE TABLE messages (
 thread_id TEXT NOT NULL REFERENCES threads(thread_id), turn_id TEXT NOT NULL,message_id TEXT NOT NULL,
 role TEXT NOT NULL CHECK(role IN ('user','assistant')),created_at_utc_ms INTEGER NOT NULL, rollout_ordinal INTEGER NOT NULL,
 PRIMARY KEY(thread_id,turn_id,message_id));
