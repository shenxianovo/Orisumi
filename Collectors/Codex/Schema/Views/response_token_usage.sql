CREATE VIEW response_token_usage AS
SELECT u.*, (
    SELECT json_extract(c.payload_json,'$.model')
    FROM rollout_events e JOIN event_contents c USING(event_id)
    WHERE (e.thread_id,e.turn_id)=(u.thread_id,u.turn_id) AND e.kind='turn_context'
      AND ((e.rollout_id=u.rollout_id AND e.source_line<u.source_line)
        OR (e.rollout_id<>u.rollout_id AND e.recorded_at_utc_ms<=u.recorded_at_utc_ms))
      AND json_extract(c.payload_json,'$.model') IS NOT NULL
    ORDER BY (e.rollout_id=u.rollout_id) DESC,e.recorded_at_utc_ms DESC,e.source_line DESC,e.event_id DESC LIMIT 1
) AS model
FROM response_token_usage_events u;
