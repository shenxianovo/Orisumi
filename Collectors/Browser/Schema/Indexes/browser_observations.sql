CREATE INDEX tab_presence_by_tab ON tab_presence_events(tab_key,observation_id);
CREATE INDEX tab_window_by_tab ON tab_window_observations(tab_key,observation_id);
CREATE INDEX tab_url_by_tab ON tab_url_observations(tab_key,observation_id);
CREATE INDEX tab_title_by_tab ON tab_title_observations(tab_key,observation_id);
CREATE INDEX window_presence_by_window ON window_presence_events(window_key,observation_id);
CREATE INDEX tab_observations_by_session ON tab_observations(browser_session,observation_id);
CREATE INDEX browser_runs_by_profile ON browser_runs(profile_id,run_id);
