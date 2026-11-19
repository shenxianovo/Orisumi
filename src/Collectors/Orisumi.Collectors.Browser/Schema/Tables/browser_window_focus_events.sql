CREATE TABLE browser_window_focus_events (
    observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),
    window_key INTEGER REFERENCES browser_windows(window_key)
);
