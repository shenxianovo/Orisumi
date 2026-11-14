CREATE VIEW active_tab_state_history AS SELECT h.*,
 (SELECT url FROM tab_url_observations f JOIN tab_observations p USING(observation_id) WHERE p.run_id=h.run_id AND f.tab_id=h.tab_id AND p.run_sequence<=h.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS url,
 (SELECT title FROM tab_title_observations f JOIN tab_observations p USING(observation_id) WHERE p.run_id=h.run_id AND f.tab_id=h.tab_id AND p.run_sequence<=h.run_sequence ORDER BY p.run_sequence DESC LIMIT 1) AS title
 FROM active_tab_history h;
