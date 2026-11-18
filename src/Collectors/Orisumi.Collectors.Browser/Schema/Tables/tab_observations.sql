CREATE TABLE tab_observations (
    observation_id INTEGER PRIMARY KEY,
    event_id TEXT NOT NULL UNIQUE,
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL CHECK(run_sequence > 0),
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK(event_kind IN ('snapshot','window_created','window_removed','window_focus_changed',
        'tab_created','tab_removed','tab_updated','tab_activated','tab_detached','tab_attached','tab_replaced')),
    UNIQUE(run_id, run_sequence)
);
