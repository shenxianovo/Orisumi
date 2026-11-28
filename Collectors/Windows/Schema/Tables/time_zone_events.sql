CREATE TABLE time_zone_events (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id), run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    time_zone_id TEXT, id_kind TEXT CHECK (id_kind IN ('iana', 'windows')),
    CHECK ((time_zone_id IS NULL) = (id_kind IS NULL)),
    PRIMARY KEY (run_id, run_sequence)
);
