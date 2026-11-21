CREATE TABLE tab_url_observations (
    observation_id INTEGER NOT NULL REFERENCES tab_observations(observation_id),
    tab_key INTEGER NOT NULL REFERENCES tabs(tab_key),
    url TEXT,
    PRIMARY KEY(tab_key, observation_id)
);
