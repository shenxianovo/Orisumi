CREATE TABLE focused_window_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'focus_changed', 'read_failed')),
    -- NULL: the focused window could not be read.
    has_window INTEGER CHECK (has_window IN (0, 1)),
    focus_id TEXT REFERENCES window_focus_instances(focus_id),
    CHECK ((focus_id IS NOT NULL) = (has_window IS 1)),
    CHECK ((event_kind = 'read_failed') = (has_window IS NULL)),
    PRIMARY KEY (run_id, run_sequence)
);
