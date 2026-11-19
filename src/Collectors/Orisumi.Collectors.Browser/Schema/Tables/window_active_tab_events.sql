CREATE TABLE window_active_tab_events (
    observation_id INTEGER NOT NULL REFERENCES tab_observations(observation_id),
    window_key INTEGER NOT NULL REFERENCES browser_windows(window_key),
    tab_key INTEGER REFERENCES tabs(tab_key),
    PRIMARY KEY(window_key,observation_id)
);
