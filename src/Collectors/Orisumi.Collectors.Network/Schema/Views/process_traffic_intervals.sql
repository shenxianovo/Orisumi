-- Expand independent directional changes at query time. Unchanged counts still
-- yield zero over the actual sample interval, without storing repeated values.
CREATE VIEW process_traffic_intervals AS
WITH observations AS (
    SELECT s.*, r.bytes AS received_bytes, t.bytes AS sent_bytes,
        r.sample_id AS received_observation_sample_id, t.sample_id AS sent_observation_sample_id
    FROM process_observed_samples s
    JOIN process_traffic_observations r ON r.process_instance_id=s.process_instance_id AND r.direction='received'
        AND r.sample_id=(SELECT MAX(n.sample_id) FROM process_traffic_observations n
            WHERE n.process_instance_id=s.process_instance_id AND n.direction='received'
                AND n.sample_id BETWEEN s.observation_start_sample_id AND s.sample_id)
    JOIN process_traffic_observations t ON t.process_instance_id=s.process_instance_id AND t.direction='sent'
        AND t.sample_id=(SELECT MAX(n.sample_id) FROM process_traffic_observations n
            WHERE n.process_instance_id=s.process_instance_id AND n.direction='sent'
                AND n.sample_id BETWEEN s.observation_start_sample_id AND s.sample_id)
), counters AS (
    SELECT *, observed_at_utc_ms AS interval_end_utc_ms,
        LAG(observed_at_utc_ms) OVER w AS interval_start_utc_ms,
        LAG(run_sequence) OVER w AS previous_sequence,
        LAG(received_bytes) OVER w AS previous_received_bytes,
        LAG(sent_bytes) OVER w AS previous_sent_bytes
    FROM observations
    WINDOW w AS (PARTITION BY process_instance_id,observation_start_sample_id ORDER BY run_sequence)
)
SELECT *,
    CASE WHEN can_compare AND continuous=1 AND previous_sequence=run_sequence-1
        AND received_bytes>=previous_received_bytes THEN received_bytes-previous_received_bytes END AS received_delta,
    CASE WHEN can_compare AND continuous=1 AND previous_sequence=run_sequence-1
        AND sent_bytes>=previous_sent_bytes THEN sent_bytes-previous_sent_bytes END AS sent_delta
FROM counters;
