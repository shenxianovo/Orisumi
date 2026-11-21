CREATE VIEW bookmark_state AS
    SELECT b.*,
    (SELECT present FROM bookmark_presence_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=9223372036854775807 ORDER BY f.observation_id DESC LIMIT 1) AS present,
    (SELECT title FROM bookmark_title_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=9223372036854775807 ORDER BY f.observation_id DESC LIMIT 1) AS title,
    (SELECT url FROM bookmark_url_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=9223372036854775807 ORDER BY f.observation_id DESC LIMIT 1) AS url,
    (SELECT (SELECT bookmark_id FROM bookmarks parent WHERE parent.bookmark_key=f.parent_key) FROM bookmark_location_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=9223372036854775807 ORDER BY f.observation_id DESC LIMIT 1) AS parent_id,
    (SELECT last_used_at_utc_ms FROM bookmark_last_used_observations f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=9223372036854775807 ORDER BY f.observation_id DESC LIMIT 1) AS last_used_at_utc_ms FROM bookmarks b;
