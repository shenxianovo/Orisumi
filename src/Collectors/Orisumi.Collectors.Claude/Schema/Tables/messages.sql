CREATE TABLE messages (
    session_id TEXT NOT NULL REFERENCES sessions(session_id), message_uuid TEXT NOT NULL,
    parent_uuid TEXT, -- '' for the first message of a chain; NULL when the source omits it.
    role TEXT NOT NULL CHECK(role IN ('user','assistant')), created_at_utc_ms INTEGER, cwd TEXT,
    response_id TEXT CHECK(role = 'assistant' OR response_id IS NULL),
    PRIMARY KEY(session_id, message_uuid));
