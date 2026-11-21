CREATE TABLE _browser_snapshots (
    profile_id TEXT NOT NULL,
    scope TEXT NOT NULL,
    browser_session TEXT NOT NULL,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    observed_at_utc_ms INTEGER NOT NULL,
    PRIMARY KEY(profile_id, scope)
);
