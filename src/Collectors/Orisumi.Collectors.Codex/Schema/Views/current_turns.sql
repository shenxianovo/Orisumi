CREATE VIEW current_turns AS
SELECT t.*, e.completed_at_utc_ms,
    CASE e.kind WHEN 'turn_started' THEN 'inProgress' WHEN 'turn_completed' THEN 'completed' WHEN 'turn_aborted' THEN 'aborted' END AS status
FROM turns t LEFT JOIN rollout_events e ON e.event_id=(
    SELECT event_id FROM rollout_events
    WHERE (thread_id,turn_id)=(t.thread_id,t.turn_id) AND kind IN ('turn_started','turn_completed','turn_aborted')
    ORDER BY COALESCE(recorded_at_utc_ms,completed_at_utc_ms,started_at_utc_ms) DESC,event_id DESC LIMIT 1
);
