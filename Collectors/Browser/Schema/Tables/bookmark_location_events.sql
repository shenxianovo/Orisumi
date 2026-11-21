CREATE TABLE bookmark_location_events (
    bookmark_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    observation_id INTEGER NOT NULL REFERENCES bookmark_observations(observation_id),
    parent_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    PRIMARY KEY(bookmark_key, observation_id)
);
