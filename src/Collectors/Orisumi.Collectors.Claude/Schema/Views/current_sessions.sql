CREATE VIEW current_sessions AS SELECT s.*,
 (SELECT title FROM session_title_observations t WHERE t.session_id=s.session_id ORDER BY observation_id DESC LIMIT 1) AS title,
 (SELECT MIN(created_at_utc_ms) FROM messages m WHERE m.session_id=s.session_id) AS first_message_at_utc_ms,
 (SELECT MAX(created_at_utc_ms) FROM messages m WHERE m.session_id=s.session_id) AS last_message_at_utc_ms FROM sessions s;
