CREATE TABLE history_page_title_observations (
    observation_id INTEGER PRIMARY KEY,
    browser TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    url TEXT NOT NULL,
    title TEXT NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    UNIQUE (browser, profile_id, url, observed_at_utc_ms, title)
);
