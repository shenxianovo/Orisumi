CREATE TABLE visits (
    browser TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    visit_id TEXT NOT NULL,
    visited_at_utc_ms INTEGER NOT NULL,
    url TEXT NOT NULL,
    transition TEXT,
    referring_visit_id TEXT,
    is_local INTEGER CHECK (is_local IN (0, 1)),
    PRIMARY KEY (browser, profile_id, visit_id)
);
