CREATE VIEW capture_coverage AS
SELECT 'connections' AS scope,'snapshot' AS mode,MIN(started_at_utc_ms) AS started_at_utc_ms,
    MAX(observed_at_utc_ms) AS stopped_at_utc_ms,'sampled' AS evidence,
    printf('%d samples',COUNT(*)) AS detail,run_id
FROM sample_windows WHERE error IS NULL
GROUP BY run_id,coverage_start_sample_id
UNION ALL
SELECT 'connections','snapshot',started_at_utc_ms,observed_at_utc_ms,'read_failed',error,run_id
FROM samples WHERE error IS NOT NULL
UNION ALL
SELECT 'collector','snapshot',started_at_utc_ms,stopped_at_utc_ms,COALESCE(stop_reason,'unconfirmed'),NULL,run_id
FROM capture_runs;
