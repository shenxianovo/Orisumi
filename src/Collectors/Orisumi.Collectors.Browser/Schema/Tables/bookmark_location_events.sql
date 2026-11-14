CREATE TABLE bookmark_location_events (
    bookmark_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    observation_id INTEGER NOT NULL REFERENCES bookmark_observations(observation_id),
    parent_id TEXT, position INTEGER CHECK(position >= 0), PRIMARY KEY(bookmark_key, observation_id)
);
