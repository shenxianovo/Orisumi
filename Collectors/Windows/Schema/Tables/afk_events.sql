CREATE TABLE afk_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'transition')),
    state TEXT CHECK (state IN ('active', 'idle')), idle_ms INTEGER CHECK (idle_ms >= 0),
    CHECK ((state IS NULL) = (idle_ms IS NULL)),
    PRIMARY KEY (run_id, run_sequence)
);
