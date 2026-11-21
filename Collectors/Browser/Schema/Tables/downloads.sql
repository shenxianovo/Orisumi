CREATE TABLE downloads (
    download_key INTEGER PRIMARY KEY,
    browser TEXT NOT NULL, profile_id TEXT NOT NULL, download_id INTEGER NOT NULL,
    url TEXT, started_at_utc_ms INTEGER,
    UNIQUE(browser, profile_id, download_id)
);
