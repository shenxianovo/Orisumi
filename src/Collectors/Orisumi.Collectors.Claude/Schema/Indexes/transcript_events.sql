CREATE INDEX transcript_events_by_parent ON transcript_events(session_id,parent_uuid);
CREATE INDEX transcript_events_by_tool_use ON transcript_events(session_id,tool_use_id) WHERE tool_use_id IS NOT NULL;
