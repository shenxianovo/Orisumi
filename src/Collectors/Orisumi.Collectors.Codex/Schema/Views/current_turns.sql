CREATE VIEW current_turns AS SELECT t.*,
    (SELECT status FROM turn_status_observations o WHERE o.thread_id=t.thread_id AND o.turn_id=t.turn_id ORDER BY observation_id DESC LIMIT 1) AS status,
    (SELECT completed_at_utc_ms FROM turn_status_observations o WHERE o.thread_id=t.thread_id AND o.turn_id=t.turn_id ORDER BY observation_id DESC LIMIT 1) AS completed_at_utc_ms
FROM turns t;
