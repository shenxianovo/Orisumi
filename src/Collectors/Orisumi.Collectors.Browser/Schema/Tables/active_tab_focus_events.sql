CREATE TABLE active_tab_focus_events (
    observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),
    has_tab INTEGER NOT NULL CHECK(has_tab IN (0,1)),
    window_id INTEGER, tab_id INTEGER,
    CHECK((window_id IS NULL) = (tab_id IS NULL)),
    CHECK(has_tab = (tab_id IS NOT NULL))
);
