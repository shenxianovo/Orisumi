CREATE TABLE download_state_observations (observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),state TEXT,ended_at_utc_ms INTEGER);
