CREATE TABLE session_title_observations (
 observation_id INTEGER PRIMARY KEY REFERENCES source_records(observation_id),session_id TEXT NOT NULL REFERENCES sessions(session_id),title TEXT NOT NULL);
