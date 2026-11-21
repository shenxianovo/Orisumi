CREATE TABLE tab_window_observations (
    observation_id INTEGER NOT NULL REFERENCES tab_observations(observation_id),
    tab_key INTEGER NOT NULL REFERENCES tabs(tab_key),
    has_window INTEGER NOT NULL CHECK(has_window IN (0,1)),
    window_key INTEGER REFERENCES browser_windows(window_key),
    CHECK(has_window = (window_key IS NOT NULL)),
    PRIMARY KEY(tab_key, observation_id)
);
