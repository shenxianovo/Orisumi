CREATE TABLE bookmark_presence_events (
    bookmark_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    observation_id INTEGER NOT NULL REFERENCES bookmark_observations(observation_id),
    present INTEGER NOT NULL CHECK(present IN (0,1)), PRIMARY KEY(bookmark_key, observation_id)
);
