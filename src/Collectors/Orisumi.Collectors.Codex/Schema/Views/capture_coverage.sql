CREATE VIEW capture_coverage AS
SELECT 'import' AS scope,'snapshot' AS mode,started_at_utc_ms,stopped_at_utc_ms,
       COALESCE(stop_reason,'unconfirmed') AS evidence,error AS detail,run_id,confirmed_until_utc_ms
FROM capture_runs
UNION ALL
SELECT COALESCE(scope,'import'),'snapshot',observed_at_utc_ms,observed_at_utc_ms,'read_failed',error,run_id,NULL
FROM capture_read_failures
UNION ALL
SELECT 'current_state','snapshot',started_at_utc_ms,stopped_at_utc_ms,
       'observed',CAST(observation_id AS TEXT),run_id,NULL
FROM database_observations
UNION ALL
SELECT 'rollout','replay',observed_at_utc_ms,observed_at_utc_ms,kind,source_path,run_id,NULL
FROM source_file_events;
