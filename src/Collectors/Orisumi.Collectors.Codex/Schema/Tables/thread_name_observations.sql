CREATE TABLE thread_name_observations (
    observation_id INTEGER PRIMARY KEY,
    thread_id TEXT NOT NULL REFERENCES threads(thread_id),
    database_observation_id INTEGER NOT NULL REFERENCES database_observations(observation_id),
    name TEXT
);
