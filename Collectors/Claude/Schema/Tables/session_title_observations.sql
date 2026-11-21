CREATE TABLE session_title_observations (
 observation_id INTEGER PRIMARY KEY REFERENCES source_records(observation_id),session_id TEXT NOT NULL REFERENCES sessions(session_id),
 kind TEXT NOT NULL CHECK(kind IN ('custom','ai')),title TEXT NOT NULL);
