CREATE VIEW current_messages AS SELECT m.*,
 (SELECT content_json FROM message_content_observations o WHERE (o.session_id,o.message_uuid)=(m.session_id,m.message_uuid) ORDER BY observation_id DESC LIMIT 1) AS content_json FROM messages m;
