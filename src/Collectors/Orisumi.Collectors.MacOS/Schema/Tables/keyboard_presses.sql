CREATE TABLE keyboard_presses (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    key_code INTEGER NOT NULL CHECK (key_code >= 0),
    pressed_at_utc_ms INTEGER NOT NULL,
    released_at_utc_ms INTEGER,
    modifiers_at_press INTEGER NOT NULL,
    PRIMARY KEY (run_id, run_sequence)
);
