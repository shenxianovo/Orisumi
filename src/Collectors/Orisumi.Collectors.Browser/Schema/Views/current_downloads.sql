CREATE VIEW current_downloads AS SELECT h.* FROM download_history h WHERE observation_id=(SELECT MAX(p.observation_id) FROM download_observations p WHERE p.download_key=h.download_key);
