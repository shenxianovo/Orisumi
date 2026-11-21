CREATE TABLE browser_windows (
    window_key INTEGER PRIMARY KEY,
    profile_id TEXT NOT NULL,
    browser_session TEXT NOT NULL,
    window_id INTEGER NOT NULL CHECK(window_id >= 0),
    UNIQUE(profile_id, browser_session, window_id)
);
