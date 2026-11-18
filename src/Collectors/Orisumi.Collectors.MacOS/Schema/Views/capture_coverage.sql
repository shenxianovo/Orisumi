CREATE VIEW capture_coverage AS
WITH channels AS (
    SELECT c.*, LEAD(observed_at_utc_ms) OVER (PARTITION BY run_id,channel ORDER BY run_sequence) AS next_at
    FROM capture_channel_events c
), methods AS (
    SELECT m.*, LEAD(observed_at_utc_ms) OVER (PARTITION BY run_id,channel ORDER BY run_sequence) AS next_at
    FROM capture_method_events m
)
SELECT 'foreground' AS scope, 'notification' AS mode, started_at_utc_ms, stopped_at_utc_ms,
    COALESCE(stop_reason,'unconfirmed') AS evidence, NULL AS detail, run_id
FROM capture_runs
UNION ALL
SELECT c.channel, CASE WHEN c.channel IN ('windows','window_title','microphone','audio_output','camera') THEN 'snapshot' ELSE 'notification' END,
    c.observed_at_utc_ms, COALESCE(c.next_at,r.stopped_at_utc_ms),
    CASE WHEN c.available=1 THEN 'available' ELSE 'unavailable' END, c.reason, c.run_id
FROM channels c JOIN capture_runs r USING(run_id)
UNION ALL
SELECT m.channel,m.mode,m.observed_at_utc_ms,COALESCE(m.next_at,r.stopped_at_utc_ms),'method_selected',NULL,m.run_id
FROM methods m JOIN capture_runs r USING(run_id);
