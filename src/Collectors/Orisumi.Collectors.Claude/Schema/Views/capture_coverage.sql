CREATE VIEW capture_coverage AS
 SELECT 'project_logs' AS scope, 'replay' AS mode, started_at_utc_ms, stopped_at_utc_ms,
 COALESCE(stop_reason,'unconfirmed') AS evidence, error AS detail, run_id, confirmed_until_utc_ms
 FROM capture_runs
 UNION ALL
 SELECT 'project_logs', 'replay', observed_at_utc_ms, observed_at_utc_ms, 'read_failed', error, run_id, NULL
 FROM capture_read_failures
 UNION ALL
 SELECT source_path, 'replay', observed_at_utc_ms, observed_at_utc_ms, kind,
 'previous offset ' || previous_offset || '; length ' || COALESCE(observed_length, 'unavailable'), run_id, NULL
 FROM source_file_events;
