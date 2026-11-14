CREATE TABLE bookmark_observations (
    observation_id INTEGER PRIMARY KEY,
    event_id TEXT NOT NULL UNIQUE,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL, observed_at_utc_ms INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('snapshot','created','changed','moved','removed','reordered','last_used')),
    UNIQUE(run_id, run_sequence)
);
