CREATE VIEW current_active_tabs AS SELECT h.* FROM active_tab_state_history h WHERE observation_id=(SELECT MAX(p.observation_id) FROM tab_observations p WHERE p.run_id=h.run_id);
