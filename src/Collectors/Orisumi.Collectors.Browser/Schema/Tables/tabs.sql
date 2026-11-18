CREATE TABLE tabs (
    tab_key INTEGER PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    tab_id INTEGER NOT NULL CHECK(tab_id >= 0),
    UNIQUE(run_id, tab_id)
);
