CREATE TABLE transcript_events (
    session_id TEXT NOT NULL REFERENCES sessions(session_id), uuid TEXT NOT NULL,
    observation_id INTEGER NOT NULL UNIQUE REFERENCES source_records(observation_id),
    parent_uuid TEXT, -- '' for a chain root; NULL when the source omits the field.
    row_type TEXT, block_type TEXT, created_at_utc_ms INTEGER, cwd TEXT, git_branch TEXT, entrypoint TEXT,
    response_id TEXT, prompt_source TEXT, permission_mode TEXT, tool_use_id TEXT,
    is_sidechain INTEGER NOT NULL CHECK(is_sidechain IN (0,1)), agent_id TEXT,
    PRIMARY KEY(session_id, uuid));
