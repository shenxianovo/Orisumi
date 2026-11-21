CREATE TABLE screen_lock_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'transition')),
    is_locked INTEGER CHECK (is_locked IN (0, 1)),
    CHECK (event_kind = 'snapshot' OR is_locked IS NOT NULL),
    PRIMARY KEY (run_id, run_sequence)
);
