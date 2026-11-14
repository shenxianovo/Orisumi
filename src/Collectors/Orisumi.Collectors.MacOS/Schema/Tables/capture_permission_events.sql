CREATE TABLE capture_permission_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    permission TEXT NOT NULL CHECK (permission IN ('accessibility', 'input_monitoring')),
    event_kind TEXT NOT NULL CHECK (event_kind IN ('lost', 'restored', 'snapshot_granted', 'snapshot_denied')),
    PRIMARY KEY (run_id, run_sequence)
);
