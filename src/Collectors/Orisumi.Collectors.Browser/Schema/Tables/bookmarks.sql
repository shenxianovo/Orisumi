CREATE TABLE bookmarks (
    bookmark_key INTEGER PRIMARY KEY,
    browser TEXT NOT NULL, profile_id TEXT NOT NULL, bookmark_id TEXT NOT NULL,
    node_kind TEXT CHECK(node_kind IN ('bookmark','folder')), added_at_utc_ms INTEGER,
    UNIQUE(browser, profile_id, bookmark_id)
);
