CREATE VIEW message_timeline AS
SELECT e.thread_id,e.turn_id,e.item_id,e.event_id,
    CASE e.item_type WHEN 'UserMessage' THEN 'user' ELSE 'assistant' END AS role,
    e.started_at_utc_ms,e.completed_at_utc_ms,e.recorded_at_utc_ms,
    e.rollout_id,f.source_path,e.source_line,e.rollout_ordinal,
    (SELECT group_concat(json_extract(value,'$.text'),char(10) ORDER BY key)
     FROM json_each(c.payload_json,'$.content') WHERE type='object') AS text,
    c.payload_json
FROM rollout_events e JOIN event_contents c USING(event_id)
JOIN source_file_events f ON f.event_id=e.source_file_event_id
WHERE e.kind='item_completed' AND e.item_type IN ('UserMessage','AgentMessage')
ORDER BY e.thread_id,e.recorded_at_utc_ms,e.rollout_id,e.source_line;
