CREATE VIEW history_pages AS SELECT o.* FROM history_page_title_observations o WHERE observation_id=(
    SELECT n.observation_id FROM history_page_title_observations n WHERE (n.browser,n.profile_id,n.url)=(o.browser,o.profile_id,o.url)
    ORDER BY n.observed_at_utc_ms DESC,n.observation_id DESC LIMIT 1);
