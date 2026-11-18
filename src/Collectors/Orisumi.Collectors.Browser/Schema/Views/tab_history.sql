CREATE VIEW tab_history AS
WITH focus AS (
    SELECT observation_id,tab_key,
           LAG(tab_key) OVER (PARTITION BY run_id ORDER BY run_sequence) AS previous_tab_key
    FROM active_tab_focus_events JOIN tab_observations USING(observation_id)
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
    WINDOW per_tab AS (PARTITION BY c.tab_key ORDER BY run_sequence)
), states AS (
    SELECT i.*,
        (SELECT f.is_present FROM tab_presence_events f JOIN tab_observations p USING(observation_id)
         WHERE f.tab_key=i.tab_key AND p.run_sequence<=i.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS is_present,
        (SELECT f.observation_id FROM tab_window_observations f JOIN tab_observations p USING(observation_id)
         WHERE f.tab_key=i.tab_key AND p.run_sequence<=i.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS location_id,
        (SELECT f.url FROM tab_url_observations f JOIN tab_observations p USING(observation_id)
         WHERE f.tab_key=i.tab_key AND p.run_sequence<=i.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS url,
        (SELECT f.title FROM tab_title_observations f JOIN tab_observations p USING(observation_id)
         WHERE f.tab_key=i.tab_key AND p.run_sequence<=i.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS title,
        (SELECT COALESCE(f.tab_key=i.tab_key,0) FROM active_tab_focus_events f JOIN tab_observations p USING(observation_id)
         WHERE p.run_id=i.run_id AND p.run_sequence<=i.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS is_focused
    FROM intervals i
), gaps AS (
    SELECT run_id,MIN(started_at_utc_ms) AS gap_at
    FROM capture_channel_events WHERE scope='tabs' AND mode='notification' AND evidence='unavailable'
    GROUP BY run_id
)
SELECT s.observation_id,s.event_id,s.run_id,s.run_sequence,s.observed_at_utc_ms,s.event_kind,
       r.browser,r.profile_id,s.tab_key,t.tab_id,s.is_present,l.has_window,w.window_id,s.url,s.title,s.is_focused,
       s.next_run_sequence,
       CASE WHEN g.gap_at IS NULL THEN s.next_at
            WHEN s.next_at IS NULL THEN MAX(s.observed_at_utc_ms,g.gap_at)
            ELSE MAX(s.observed_at_utc_ms,MIN(s.next_at,g.gap_at)) END AS valid_until_utc_ms
FROM states s JOIN tabs t USING(tab_key) JOIN capture_runs r ON r.run_id=s.run_id
LEFT JOIN tab_window_observations l ON l.tab_key=s.tab_key AND l.observation_id=s.location_id
LEFT JOIN browser_windows w USING(window_key)
LEFT JOIN gaps g ON g.run_id=s.run_id
WHERE EXISTS (SELECT 1 FROM tab_observations b WHERE b.run_id=s.run_id AND b.run_sequence=1 AND b.event_kind='snapshot');
