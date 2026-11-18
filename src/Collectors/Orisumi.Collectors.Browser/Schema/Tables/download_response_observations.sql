CREATE TABLE download_response_observations (
    observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),
    final_url TEXT, total_bytes INTEGER CHECK(total_bytes >= 0), mime_type TEXT
);
