CREATE TABLE download_state_observations (
    observation_id INTEGER PRIMARY KEY REFERENCES download_observations(observation_id),
    state TEXT CHECK(state IN ('in_progress','interrupted','complete')),
    ended_at_utc_ms INTEGER,
    CHECK(ended_at_utc_ms IS NULL OR state IS NOT 'in_progress')
);
