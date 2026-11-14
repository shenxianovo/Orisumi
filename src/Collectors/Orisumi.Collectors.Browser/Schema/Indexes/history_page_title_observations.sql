CREATE INDEX history_titles_by_url ON history_page_title_observations(browser,profile_id,url,observation_id);
CREATE UNIQUE INDEX history_title_samples ON history_page_title_observations(browser,profile_id,url,observed_at_utc_ms,title) WHERE event_id IS NULL;
