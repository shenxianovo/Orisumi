CREATE TABLE display_sleep_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'transition')),
    is_asleep INTEGER CHECK (is_asleep IN (0, 1)),
    CHECK (event_kind = 'snapshot' OR is_asleep IS NOT NULL),
    PRIMARY KEY (run_id, run_sequence)
);
