CREATE VIEW capture_coverage AS
 WITH transitions AS (
  SELECT *, LEAD(started_at_utc_ms) OVER (
   PARTITION BY browser,profile_id,scope,mode,run_id ORDER BY started_at_utc_ms,rowid) AS next_at
  FROM capture_channel_events WHERE stopped_at_utc_ms IS NULL
 )
 SELECT scope,mode,started_at_utc_ms,next_at AS stopped_at_utc_ms,evidence,detail,browser,profile_id,run_id
 FROM transitions
 UNION ALL
 SELECT scope,mode,started_at_utc_ms,stopped_at_utc_ms,evidence,detail,browser,profile_id,run_id
 FROM capture_channel_events WHERE stopped_at_utc_ms IS NOT NULL;
