CREATE TABLE bookmark_last_used_observations (
    bookmark_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    observation_id INTEGER NOT NULL REFERENCES bookmark_observations(observation_id),
    last_used_at_utc_ms INTEGER NOT NULL, PRIMARY KEY(bookmark_key, observation_id)
);
