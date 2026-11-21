CREATE VIEW current_threads AS
SELECT t.*,
    (SELECT name FROM thread_name_observations o WHERE o.thread_id=t.thread_id ORDER BY observation_id DESC LIMIT 1) AS name,
    (SELECT archived FROM thread_archive_observations o WHERE o.thread_id=t.thread_id ORDER BY observation_id DESC LIMIT 1) AS archived,
    (SELECT is_pinned FROM thread_pin_observations o WHERE o.thread_id=t.thread_id ORDER BY observation_id DESC LIMIT 1) AS is_pinned
FROM threads t;
