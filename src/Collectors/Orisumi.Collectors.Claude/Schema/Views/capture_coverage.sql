CREATE VIEW capture_coverage AS
 SELECT 'project_logs' AS scope, 'replay' AS mode, started_at_utc_ms, stopped_at_utc_ms,
 CASE WHEN stop_reason='failed' THEN 'read_failed' WHEN stopped_at_utc_ms IS NULL THEN 'unconfirmed' ELSE 'imported' END AS evidence,
 COALESCE(error, records_read || ' records; ' || bytes_read || ' bytes read') AS detail, run_id
 FROM capture_runs
 UNION ALL
 SELECT source_path, 'replay', observed_at_utc_ms, observed_at_utc_ms, kind,
 'previous offset ' || previous_offset || '; length ' || COALESCE(observed_length, 'unavailable'), run_id
 FROM source_file_events;
