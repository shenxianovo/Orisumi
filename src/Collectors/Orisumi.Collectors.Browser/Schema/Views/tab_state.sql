CREATE VIEW tab_state AS
SELECT t.*,
       (SELECT is_present FROM tab_presence_events WHERE tab_key=t.tab_key ORDER BY observation_id DESC LIMIT 1) AS is_present,
       (SELECT window_key FROM tab_window_observations WHERE tab_key=t.tab_key ORDER BY observation_id DESC LIMIT 1) AS window_key,
       (SELECT has_window FROM tab_window_observations WHERE tab_key=t.tab_key ORDER BY observation_id DESC LIMIT 1) AS has_window,
       (SELECT url FROM tab_url_observations WHERE tab_key=t.tab_key ORDER BY observation_id DESC LIMIT 1) AS url,
       (SELECT title FROM tab_title_observations WHERE tab_key=t.tab_key ORDER BY observation_id DESC LIMIT 1) AS title
FROM tabs t;
