CREATE TABLE browser_window_focus_events (
    observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),
    has_window INTEGER CHECK(has_window IN (0,1)),
    window_key INTEGER REFERENCES browser_windows(window_key),
    CHECK((has_window IS 1) = (window_key IS NOT NULL))
);
