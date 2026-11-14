CREATE TABLE active_tab_focus_events (
 observation_id INTEGER PRIMARY KEY REFERENCES tab_observations(observation_id),window_id INTEGER,tab_id INTEGER,
 CHECK((window_id IS NULL)=(tab_id IS NULL)));
