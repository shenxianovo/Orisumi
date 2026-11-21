CREATE TABLE thread_archive_observations (
    observation_id INTEGER PRIMARY KEY,
    thread_id TEXT NOT NULL REFERENCES threads(thread_id),
    database_observation_id INTEGER NOT NULL REFERENCES database_observations(observation_id),
    archived INTEGER CHECK(archived IN (0,1))
);
