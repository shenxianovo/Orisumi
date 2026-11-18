CREATE TABLE capture_runs (
    run_id INTEGER PRIMARY KEY,
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER,
    scope TEXT NOT NULL CHECK(scope IN ('all','thread')),
    thread_filter TEXT,
    trigger TEXT NOT NULL,
    stop_reason TEXT CHECK(stop_reason IN ('completed','failed')),
    error TEXT,
    host_observation_id INTEGER REFERENCES host_observations(observation_id),
    CHECK((scope = 'thread') = (thread_filter IS NOT NULL)),
    CHECK((stopped_at_utc_ms IS NULL) = (stop_reason IS NULL)),
    CHECK((stop_reason IS 'failed') = (error IS NOT NULL))
);
