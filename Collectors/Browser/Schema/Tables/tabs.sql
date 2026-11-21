CREATE TABLE tabs (
    tab_key INTEGER PRIMARY KEY,
    profile_id TEXT NOT NULL,
    browser_session TEXT NOT NULL,
    tab_id INTEGER NOT NULL CHECK(tab_id >= 0),
    UNIQUE(profile_id, browser_session, tab_id)
);
