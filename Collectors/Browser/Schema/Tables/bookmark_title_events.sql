CREATE TABLE bookmark_title_events (
    bookmark_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    observation_id INTEGER NOT NULL REFERENCES bookmark_observations(observation_id),
    title TEXT NOT NULL, PRIMARY KEY(bookmark_key, observation_id)
);
