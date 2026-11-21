CREATE TABLE threads (
    thread_id TEXT PRIMARY KEY NOT NULL,
    created_at_utc_ms INTEGER,
    cwd TEXT,
    git_branch TEXT,
    git_sha TEXT,
    git_origin_url TEXT,
    source TEXT,
    originator TEXT,
    parent_thread_id TEXT
);
