CREATE TABLE download_observations (
    observation_id INTEGER PRIMARY KEY,
    event_id TEXT NOT NULL UNIQUE,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL, observed_at_utc_ms INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('snapshot','created','changed','erased','snapshot_missing')),
    download_key INTEGER NOT NULL REFERENCES downloads(download_key),
    UNIQUE(run_id, run_sequence, download_key)
);
