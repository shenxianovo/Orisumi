CREATE INDEX time_zone_events_by_run_and_time
    ON time_zone_events (run_id, observed_at_utc_ms, run_sequence);
