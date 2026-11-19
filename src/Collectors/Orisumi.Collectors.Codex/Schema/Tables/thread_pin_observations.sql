CREATE TABLE thread_pin_observations (
    observation_id INTEGER PRIMARY KEY,
    thread_id TEXT NOT NULL REFERENCES threads(thread_id),
    database_observation_id INTEGER NOT NULL REFERENCES database_observations(observation_id),
    is_pinned INTEGER CHECK(is_pinned IN (0,1))
);
