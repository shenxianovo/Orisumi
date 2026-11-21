CREATE TABLE download_path_observations (observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),local_path TEXT);
