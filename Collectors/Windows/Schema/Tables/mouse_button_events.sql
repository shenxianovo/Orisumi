CREATE TABLE mouse_button_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('button_down', 'button_up')),
    -- Cursor position in physical pixels of the virtual screen when the event was read; NULL when it could not be read.
    x_pixels INTEGER,
    y_pixels INTEGER,
    -- 0 left, 1 right, 2 middle, 3 X1, 4 X2.
    button INTEGER NOT NULL CHECK (button BETWEEN 0 AND 4),
    CHECK ((x_pixels IS NULL) = (y_pixels IS NULL)),
    PRIMARY KEY (run_id, run_sequence)
);
