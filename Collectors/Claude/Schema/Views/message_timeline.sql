CREATE VIEW message_timeline AS
 WITH RECURSIVE
 events AS NOT MATERIALIZED (
    SELECT e.session_id,e.uuid,e.parent_uuid,e.observation_id,
           CASE WHEN e.row_type IN ('user','assistant') AND e.block_type IN ('text','image','document')
                     AND c.observation_id IS NOT NULL THEN e.uuid END AS visible_uuid
    FROM transcript_events e LEFT JOIN event_contents c USING(observation_id)
 ),
 chain(session_id,uuid,root_uuid,depth,chain_order,visible_parent_uuid,last_visible_uuid) AS (
    SELECT e.session_id,e.uuid,e.uuid,0,printf('%020lld',e.observation_id),NULL,e.visible_uuid
    FROM events e
    WHERE e.parent_uuid IS NULL OR e.parent_uuid='' OR NOT EXISTS (
        SELECT 1 FROM transcript_events p WHERE (p.session_id,p.uuid)=(e.session_id,e.parent_uuid)
    )
    UNION ALL
    SELECT e.session_id,e.uuid,p.root_uuid,p.depth+1,p.chain_order || '/' || printf('%020lld',e.observation_id),
           p.last_visible_uuid,COALESCE(e.visible_uuid,p.last_visible_uuid)
    FROM chain p JOIN events e ON (e.session_id,e.parent_uuid)=(p.session_id,p.uuid) AND e.parent_uuid!=''
 )
 SELECT e.*,chain.root_uuid,chain.depth,chain.chain_order,chain.visible_parent_uuid,
        json_extract(c.payload_json,'$.text') AS text,c.payload_json
 FROM chain JOIN transcript_events e USING(session_id,uuid) JOIN event_contents c USING(observation_id)
 WHERE chain.last_visible_uuid=chain.uuid
 ORDER BY e.session_id,chain.chain_order;
