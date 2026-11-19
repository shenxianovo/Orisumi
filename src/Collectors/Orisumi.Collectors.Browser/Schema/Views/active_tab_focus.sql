CREATE VIEW active_tab_focus AS
SELECT o.observation_id,o.run_id,o.browser_session,r.profile_id,
       CASE f.has_window WHEN 0 THEN 0 WHEN 1 THEN a.has_tab END AS has_tab,a.tab_key,
       f.observation_id AS window_focus_observation_id,a.observation_id AS window_active_observation_id
FROM tab_observations o JOIN browser_runs r USING(run_id)
LEFT JOIN browser_window_focus_events f ON f.observation_id=(
    -- Seek backward through window focus facts, which are sparser than tab observations.
    SELECT p.observation_id FROM browser_window_focus_events prior
    CROSS JOIN tab_observations p USING(observation_id) CROSS JOIN browser_runs pr USING(run_id)
    WHERE prior.observation_id<=o.observation_id AND pr.profile_id=r.profile_id AND p.browser_session=o.browser_session
    ORDER BY prior.observation_id DESC LIMIT 1)
LEFT JOIN window_active_tab_events a ON a.window_key=f.window_key AND a.observation_id=(
    SELECT MAX(observation_id) FROM window_active_tab_events
    WHERE window_key=f.window_key AND observation_id<=o.observation_id);
