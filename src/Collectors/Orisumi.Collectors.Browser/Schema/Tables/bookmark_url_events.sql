CREATE TABLE bookmark_url_events (
    bookmark_key INTEGER NOT NULL REFERENCES bookmarks(bookmark_key),
    observation_id INTEGER NOT NULL REFERENCES bookmark_observations(observation_id),
    url TEXT NOT NULL, PRIMARY KEY(bookmark_key, observation_id)
);
