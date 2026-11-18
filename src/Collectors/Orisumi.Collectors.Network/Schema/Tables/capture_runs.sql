CREATE TABLE capture_runs (
    run_id TEXT PRIMARY KEY NOT NULL,
    source TEXT NOT NULL CHECK(source = 'nettop'),
    boot_id TEXT NOT NULL,
    interval_seconds INTEGER NOT NULL CHECK(interval_seconds > 0),
    started_at_utc_ms INTEGER NOT NULL,
    stopped_at_utc_ms INTEGER,
    stop_reason TEXT CHECK(stop_reason IN ('stopped','failed')),
    CHECK((stopped_at_utc_ms IS NULL) = (stop_reason IS NULL))
);
