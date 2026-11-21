CREATE VIEW capture_coverage AS
SELECT 'browser' AS scope,'notification' AS mode,r.started_at_utc_ms,r.stopped_at_utc_ms,
       COALESCE(r.stop_reason,'unconfirmed') AS evidence,r.error AS detail,
       b.browser,b.profile_id,r.run_id,r.confirmed_until_utc_ms
FROM capture_runs r JOIN browser_runs b USING(run_id)
UNION ALL
SELECT e.scope,e.mode,e.started_at_utc_ms,e.stopped_at_utc_ms,e.evidence,e.detail,
       b.browser,b.profile_id,e.run_id,NULL
FROM capture_channel_events e JOIN browser_runs b USING(run_id)
UNION ALL
SELECT 'browser','notification',f.observed_at_utc_ms,f.observed_at_utc_ms,'read_failed',f.error,
       b.browser,b.profile_id,f.run_id,NULL
FROM capture_read_failures f JOIN browser_runs b USING(run_id)
UNION ALL
SELECT d.scope,CASE WHEN d.scope='history' THEN 'replay' ELSE 'notification' END,
       d.observed_at_utc_ms,d.observed_at_utc_ms,'delivery_rejected',d.error,
       b.browser,b.profile_id,d.run_id,NULL
FROM browser_rejected_deliveries d JOIN browser_runs b USING(run_id)
UNION ALL
SELECT 'history','replay',backfill_before_utc_ms,forward_until_utc_ms,'range_read',
       'URLs selected by latest visit; retained records only','chrome',profile_id,NULL,NULL
FROM _history_cursors WHERE backfill_before_utc_ms < forward_until_utc_ms;
