CREATE TABLE capture_runs (
    run_id INTEGER PRIMARY KEY,
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER,
    stop_reason TEXT CHECK(stop_reason IN ('completed','failed')),
    error TEXT,
    confirmed_until_utc_ms INTEGER,
    host_observation_id INTEGER NOT NULL REFERENCES host_observations(observation_id),
    CHECK((stopped_at_utc_ms IS NULL) = (stop_reason IS NULL)),
    CHECK((stop_reason IS 'failed') = (error IS NOT NULL))
);
