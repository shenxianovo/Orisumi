CREATE TABLE event_contents (
    observation_id INTEGER PRIMARY KEY REFERENCES transcript_events(observation_id),
    payload_json TEXT NOT NULL);
