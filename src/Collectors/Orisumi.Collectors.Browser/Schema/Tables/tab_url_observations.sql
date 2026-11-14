CREATE TABLE tab_url_observations (
 observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),tab_id INTEGER NOT NULL,url TEXT NOT NULL);
