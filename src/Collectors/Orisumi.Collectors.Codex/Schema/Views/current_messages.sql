CREATE VIEW current_messages AS SELECT m.*,
 (SELECT item_json FROM message_content_observations o WHERE (o.thread_id,o.turn_id,o.message_id)=(m.thread_id,m.turn_id,m.message_id) ORDER BY observation_id DESC LIMIT 1) AS item_json FROM messages m;
