CREATE TABLE event_contents (
    event_id INTEGER PRIMARY KEY REFERENCES rollout_events(event_id),
    payload_json TEXT NOT NULL
);
