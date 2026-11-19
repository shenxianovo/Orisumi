CREATE VIEW current_tabs AS
WITH latest AS (
    SELECT profile_id,browser_session FROM browser_runs b
    WHERE b.run_id=(SELECT MAX(run_id) FROM browser_runs WHERE profile_id=b.profile_id)
)
SELECT t.*,w.window_id,'chrome' AS browser,
       (SELECT COALESCE(f.tab_key=t.tab_key,0) FROM active_tab_focus_events f
        JOIN tab_observations o USING(observation_id) JOIN browser_runs r USING(run_id)
        WHERE r.profile_id=t.profile_id AND o.browser_session=t.browser_session
        ORDER BY observation_id DESC LIMIT 1) AS is_focused
FROM tab_state t JOIN latest USING(profile_id,browser_session)
LEFT JOIN browser_windows w USING(window_key)
WHERE t.is_present=1;
