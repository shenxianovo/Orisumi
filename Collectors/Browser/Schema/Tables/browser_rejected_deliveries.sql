CREATE TABLE browser_rejected_deliveries (
    run_id INTEGER NOT NULL REFERENCES browser_runs(run_id),
    profile_id TEXT NOT NULL,
    queue_id TEXT NOT NULL,
    sequence INTEGER NOT NULL CHECK(sequence > 0),
    scope TEXT NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    error TEXT NOT NULL,
    PRIMARY KEY(profile_id, queue_id, sequence)
);
