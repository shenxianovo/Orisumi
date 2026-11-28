CREATE TABLE keyboard_presses (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    -- Raw Input RAWKEYBOARD.VKey and MakeCode; is_extended is the E0 or E1 prefix.
    vk_code INTEGER NOT NULL CHECK (vk_code BETWEEN 0 AND 254),
    scan_code INTEGER NOT NULL CHECK (scan_code >= 0),
    is_extended INTEGER NOT NULL CHECK (is_extended IN (0, 1)),
    pressed_at_utc_ms INTEGER NOT NULL,
    -- NULL when the release was not seen: the run stopped or observation was interrupted first.
    released_at_utc_ms INTEGER,
    -- Bit 1 Shift, 2 Ctrl, 4 Alt, 8 Windows key, as held when this key went down.
    modifiers_at_press INTEGER NOT NULL CHECK (modifiers_at_press BETWEEN 0 AND 15),
    PRIMARY KEY (run_id, run_sequence)
);
