-- A gap requires a new initial observation, even for the same verified process.
CREATE VIEW process_observed_samples AS
WITH presence AS (
    SELECT *, LEAD(sample_id) OVER (PARTITION BY process_instance_id ORDER BY sample_id) AS next_sample_id
    FROM process_presence_events
)
SELECT e.process_instance_id, e.sample_id AS observation_start_sample_id,
    s.sample_id,s.run_id,s.run_sequence,s.observed_at_utc_ms,s.continuous,
    (p.started_at_utc_us IS NOT NULL) AS can_compare
FROM presence e
JOIN process_instances p USING(process_instance_id)
JOIN sample_windows s ON s.run_id=p.run_id
    AND s.sample_id>=e.sample_id AND (e.next_sample_id IS NULL OR s.sample_id<e.next_sample_id)
    AND s.coverage_start_sample_id<=e.sample_id
WHERE e.present=1 AND s.error IS NULL;
