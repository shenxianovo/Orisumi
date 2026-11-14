CREATE TABLE messages (
 session_id TEXT NOT NULL REFERENCES sessions(session_id),message_uuid TEXT NOT NULL,parent_uuid TEXT,
 role TEXT NOT NULL CHECK(role IN ('user','assistant')),created_at_utc_ms INTEGER NOT NULL,cwd TEXT,response_id TEXT,
 PRIMARY KEY(session_id,message_uuid));
