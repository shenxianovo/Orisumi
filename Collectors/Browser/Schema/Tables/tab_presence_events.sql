CREATE TABLE tab_presence_events (
    observation_id INTEGER NOT NULL REFERENCES tab_observations(observation_id),
    tab_key INTEGER NOT NULL REFERENCES tabs(tab_key),
    is_present INTEGER NOT NULL CHECK(is_present IN (0,1)),
    PRIMARY KEY(tab_key, observation_id)
);
