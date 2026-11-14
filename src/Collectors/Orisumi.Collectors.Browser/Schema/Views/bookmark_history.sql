CREATE VIEW bookmark_history AS
    WITH changes AS (
        SELECT bookmark_key, observation_id FROM bookmark_presence_events
        UNION SELECT bookmark_key, observation_id FROM bookmark_title_events
        UNION SELECT bookmark_key, observation_id FROM bookmark_url_events
        UNION SELECT bookmark_key, observation_id FROM bookmark_location_events
        UNION SELECT bookmark_key, observation_id FROM bookmark_last_used_observations
    )
    SELECT b.*,
    (SELECT present FROM bookmark_presence_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=c.observation_id ORDER BY f.observation_id DESC LIMIT 1) AS present,
    (SELECT title FROM bookmark_title_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=c.observation_id ORDER BY f.observation_id DESC LIMIT 1) AS title,
    (SELECT url FROM bookmark_url_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=c.observation_id ORDER BY f.observation_id DESC LIMIT 1) AS url,
    (SELECT parent_id FROM bookmark_location_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=c.observation_id ORDER BY f.observation_id DESC LIMIT 1) AS parent_id,
    (SELECT position FROM bookmark_location_events f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=c.observation_id ORDER BY f.observation_id DESC LIMIT 1) AS position,
    (SELECT last_used_at_utc_ms FROM bookmark_last_used_observations f WHERE f.bookmark_key=b.bookmark_key AND f.observation_id<=c.observation_id ORDER BY f.observation_id DESC LIMIT 1) AS last_used_at_utc_ms, c.observation_id, o.event_id, o.run_id, o.run_sequence,
        o.observed_at_utc_ms, o.kind
    FROM changes c JOIN bookmarks b USING(bookmark_key)
        JOIN bookmark_observations o USING(observation_id);
