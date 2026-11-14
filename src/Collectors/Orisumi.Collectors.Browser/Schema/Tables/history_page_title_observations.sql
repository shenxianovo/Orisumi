CREATE TABLE history_page_title_observations (
    observation_id INTEGER PRIMARY KEY,
    event_id TEXT UNIQUE,
    browser TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    url TEXT NOT NULL,
    title TEXT NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL
);
