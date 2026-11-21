CREATE TABLE window_presence_events (
    observation_id INTEGER NOT NULL REFERENCES tab_observations(observation_id),
    window_key INTEGER NOT NULL REFERENCES browser_windows(window_key),
    is_present INTEGER NOT NULL CHECK(is_present IN (0,1)),
    PRIMARY KEY(window_key, observation_id)
);
