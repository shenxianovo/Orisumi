CREATE TABLE turns (thread_id TEXT NOT NULL REFERENCES threads(thread_id), turn_id TEXT NOT NULL, PRIMARY KEY(thread_id,turn_id));
