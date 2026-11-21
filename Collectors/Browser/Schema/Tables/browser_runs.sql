CREATE TABLE browser_runs (
    run_id INTEGER PRIMARY KEY REFERENCES capture_runs(run_id),
    browser TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    browser_session TEXT NOT NULL,
    extension_version TEXT
);
