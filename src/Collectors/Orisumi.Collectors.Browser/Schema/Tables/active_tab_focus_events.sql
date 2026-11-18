CREATE TABLE active_tab_focus_events (
    observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),
    has_tab INTEGER NOT NULL CHECK(has_tab IN (0,1)),
    tab_key INTEGER REFERENCES tabs(tab_key),
    CHECK(has_tab = (tab_key IS NOT NULL))
);
