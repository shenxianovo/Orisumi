CREATE TABLE tab_title_observations (
 observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),tab_id INTEGER NOT NULL,title TEXT NOT NULL);
