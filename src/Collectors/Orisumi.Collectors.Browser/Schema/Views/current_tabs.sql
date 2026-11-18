CREATE VIEW current_tabs AS
WITH baselines AS (
    SELECT r.run_id,ROW_NUMBER() OVER (PARTITION BY r.browser,r.profile_id
        ORDER BY r.started_at_utc_ms DESC,o.observation_id DESC) AS recency
    FROM capture_runs r JOIN tab_observations o USING(run_id)
    WHERE r.scope='tabs' AND o.event_kind='snapshot'
)
SELECT h.* FROM tab_history h JOIN baselines b USING(run_id)
WHERE b.recency=1 AND h.is_present=1 AND h.next_run_sequence IS NULL;
