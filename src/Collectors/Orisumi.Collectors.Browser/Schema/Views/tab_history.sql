CREATE VIEW tab_history AS
WITH focus AS (
    SELECT observation_id,tab_key,
           LAG(tab_key) OVER (PARTITION BY profile_id,o.browser_session ORDER BY observation_id) AS previous_tab_key
    FROM active_tab_focus_events JOIN tab_observations o USING(observation_id) JOIN browser_runs USING(run_id)
), changes AS (
    SELECT observation_id,tab_key FROM tab_presence_events
    UNION SELECT observation_id,tab_key FROM tab_window_observations
    UNION SELECT observation_id,tab_key FROM tab_url_observations
    UNION SELECT observation_id,tab_key FROM tab_title_observations
    UNION SELECT observation_id,tab_key FROM focus WHERE tab_key IS NOT NULL
    UNION SELECT observation_id,previous_tab_key FROM focus WHERE previous_tab_key IS NOT NULL
), intervals AS (
    SELECT o.*,c.tab_key,
           LEAD(run_sequence) OVER per_tab AS next_run_sequence,
           LEAD(observed_at_utc_ms) OVER per_tab AS next_at
    FROM changes c JOIN tab_observations o USING(observation_id)
    WINDOW per_tab AS (PARTITION BY c.tab_key,run_id ORDER BY observation_id)
)
SELECT i.observation_id,i.event_id,i.run_id,i.run_sequence,i.observed_at_utc_ms,i.event_kind,
       b.browser,b.profile_id,i.tab_key,t.tab_id,
       (SELECT is_present FROM tab_presence_events WHERE tab_key=i.tab_key AND observation_id<=i.observation_id ORDER BY observation_id DESC LIMIT 1) AS is_present,
       l.has_window,w.window_id,
       (SELECT url FROM tab_url_observations WHERE tab_key=i.tab_key AND observation_id<=i.observation_id ORDER BY observation_id DESC LIMIT 1) AS url,
       (SELECT title FROM tab_title_observations WHERE tab_key=i.tab_key AND observation_id<=i.observation_id ORDER BY observation_id DESC LIMIT 1) AS title,
       (SELECT COALESCE(f.tab_key=i.tab_key,0) FROM active_tab_focus_events f
        JOIN tab_observations o USING(observation_id) WHERE o.browser_session=i.browser_session
           AND o.run_id=i.run_id AND o.observation_id<=i.observation_id ORDER BY o.observation_id DESC LIMIT 1) AS is_focused,
       i.next_run_sequence,
       (SELECT MAX(i.observed_at_utc_ms,MIN(until)) FROM (
           SELECT i.next_at AS until UNION ALL SELECT r.stopped_at_utc_ms
           UNION ALL SELECT e.started_at_utc_ms FROM capture_channel_events e
             WHERE e.run_id=i.run_id AND e.scope='tabs'
               AND e.evidence IN ('snapshot_timeout','reconciliation_changed')
               AND (e.stopped_at_utc_ms IS NULL OR e.stopped_at_utc_ms>i.observed_at_utc_ms)
       )) AS valid_until_utc_ms
FROM intervals i JOIN tabs t USING(tab_key) JOIN browser_runs b ON b.run_id=i.run_id
JOIN capture_runs r ON r.run_id=i.run_id
LEFT JOIN tab_window_observations l ON l.tab_key=i.tab_key AND l.observation_id=(
    SELECT observation_id FROM tab_window_observations
    WHERE tab_key=i.tab_key AND observation_id<=i.observation_id ORDER BY observation_id DESC LIMIT 1)
LEFT JOIN browser_windows w USING(window_key);
