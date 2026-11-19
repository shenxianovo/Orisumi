CREATE TABLE user_session_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('session_active', 'session_inactive')),
    PRIMARY KEY (run_id, run_sequence)
);
