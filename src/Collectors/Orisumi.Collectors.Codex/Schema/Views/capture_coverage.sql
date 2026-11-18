CREATE VIEW capture_coverage AS
SELECT 'current_state' AS scope,'snapshot' AS mode,started_at_utc_ms,stopped_at_utc_ms,
       'observed' AS evidence,CAST(run_id AS TEXT) AS detail,run_id
FROM database_observations
UNION ALL
SELECT 'import','snapshot',started_at_utc_ms,stopped_at_utc_ms,
       CASE WHEN stopped_at_utc_ms IS NULL THEN 'unconfirmed' ELSE 'failed' END,error,run_id
FROM capture_runs WHERE stopped_at_utc_ms IS NULL OR stop_reason='failed'
UNION ALL
SELECT 'usage','replay',started_at_utc_ms,stopped_at_utc_ms,
       CASE WHEN read_kind='appended' THEN 'read' ELSE read_kind END,
       json_object('path',source_path,'start_offset',start_offset,'end_offset',end_offset,'bytes_read',bytes_read),run_id
FROM usage_reads
UNION ALL
SELECT 'usage','replay',observed_at_utc_ms,observed_at_utc_ms,
       CASE WHEN available=1 THEN 'source_available' ELSE 'source_missing' END,source_path,run_id
FROM usage_source_events
UNION ALL
SELECT 'current_state','snapshot',
       COALESCE((SELECT o.stopped_at_utc_ms FROM database_observations o WHERE o.run_id<r.run_id ORDER BY o.run_id DESC LIMIT 1),r.started_at_utc_ms),
       (SELECT o.stopped_at_utc_ms FROM database_observations o WHERE o.run_id>=r.run_id ORDER BY o.run_id LIMIT 1),
       'watch_gap',r.trigger,r.run_id
FROM capture_runs r WHERE r.trigger IN ('startup','watcher_error','retry');
