CREATE VIEW active_tab_history AS SELECT o.*,r.browser,r.profile_id,
 (SELECT window_id FROM active_tab_focus_events f JOIN tab_observations p USING(observation_id) WHERE p.run_id=o.run_id AND p.run_sequence<=o.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS window_id,
 (SELECT tab_id FROM active_tab_focus_events f JOIN tab_observations p USING(observation_id) WHERE p.run_id=o.run_id AND p.run_sequence<=o.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS tab_id
 FROM tab_observations o JOIN capture_runs r USING(run_id);
