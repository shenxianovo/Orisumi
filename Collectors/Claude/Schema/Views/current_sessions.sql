CREATE VIEW current_sessions AS
 SELECT s.*,
 (SELECT title FROM session_title_observations t WHERE t.session_id=s.session_id AND kind='custom' ORDER BY observation_id DESC LIMIT 1) AS custom_title,
 (SELECT title FROM session_title_observations t WHERE t.session_id=s.session_id AND kind='ai' ORDER BY observation_id DESC LIMIT 1) AS ai_title,
 messages.first_message_at_utc_ms, messages.last_message_at_utc_ms
 FROM sessions s
 LEFT JOIN (
    SELECT e.session_id, MIN(e.created_at_utc_ms) AS first_message_at_utc_ms, MAX(e.created_at_utc_ms) AS last_message_at_utc_ms
    FROM transcript_events e JOIN event_contents c USING(observation_id)
    WHERE e.block_type IN ('text','image','document') AND e.row_type IN ('user','assistant')
    GROUP BY e.session_id
 ) messages USING(session_id);
