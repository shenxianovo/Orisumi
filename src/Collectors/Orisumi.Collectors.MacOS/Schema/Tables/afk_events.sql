CREATE TABLE afk_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'transition')),
    state TEXT NOT NULL CHECK (state IN ('active', 'idle')),
    idle_ms INTEGER NOT NULL CHECK (idle_ms >= 0),
    PRIMARY KEY (run_id, run_sequence)
);
