CREATE VIEW window_history AS
WITH events AS (
    SELECT run_id,run_sequence,observed_at_utc_ms,event_kind,focus_id FROM focused_window_events
    UNION ALL SELECT t.run_id,t.run_sequence,t.observed_at_utc_ms,'title_changed',t.focus_id FROM window_title_observations t
    WHERE NOT EXISTS (SELECT 1 FROM focused_window_events f WHERE f.run_id=t.run_id AND f.run_sequence=t.run_sequence)
)
SELECT e.*,p.pid,
    (SELECT title FROM window_title_observations t WHERE t.focus_id=e.focus_id AND t.run_sequence<=e.run_sequence ORDER BY t.run_sequence DESC LIMIT 1) AS window_title
FROM events e LEFT JOIN window_focus_instances f USING(focus_id) LEFT JOIN process_instances p USING(process_instance_id);
