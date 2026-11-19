CREATE TABLE mouse_button_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('button_down', 'button_up')),
    x_points REAL NOT NULL,
    y_points REAL NOT NULL,
    button INTEGER NOT NULL CHECK (button >= 0),
    PRIMARY KEY (run_id, run_sequence)
);
