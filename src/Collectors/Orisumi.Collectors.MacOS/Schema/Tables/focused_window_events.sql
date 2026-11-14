CREATE TABLE focused_window_events (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'focus_changed')),
    focus_id TEXT REFERENCES window_focus_instances(focus_id),
    PRIMARY KEY (run_id, run_sequence)
);
