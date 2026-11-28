CREATE VIEW taskbar_app_intervals AS
WITH changes AS (
    SELECT run_id, run_sequence, observed_at_utc_ms, process_instance_id, has_taskbar_window
    FROM taskbar_window_observations
    UNION ALL
    SELECT run_id, run_sequence, observed_at_utc_ms, process_instance_id, NULL
    FROM running_app_observations WHERE event_kind = 'disappeared'
), intervals AS (
    SELECT *, LEAD(observed_at_utc_ms) OVER (PARTITION BY run_id, process_instance_id ORDER BY run_sequence) AS next_at
    FROM changes
)
SELECT i.run_id, i.run_sequence, i.process_instance_id, p.pid, p.app_name, p.aumid, p.executable_path,
    i.observed_at_utc_ms AS started_at_utc_ms,
    (SELECT MIN(boundary) FROM (
        SELECT i.next_at AS boundary
        UNION ALL SELECT COALESCE(r.stopped_at_utc_ms, r.confirmed_until_utc_ms)
        UNION ALL SELECT observed_at_utc_ms FROM capture_channel_events c WHERE c.run_id = i.run_id
            AND c.channel = 'applications' AND c.available = 0 AND c.run_sequence > i.run_sequence
    )) AS stopped_at_utc_ms
FROM intervals i JOIN process_instances p USING(process_instance_id) JOIN capture_runs r ON r.run_id=i.run_id
WHERE i.has_taskbar_window = 1;
