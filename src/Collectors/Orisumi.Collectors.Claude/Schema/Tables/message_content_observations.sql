CREATE TABLE message_content_observations (
 observation_id INTEGER NOT NULL REFERENCES source_records(observation_id),session_id TEXT NOT NULL,message_uuid TEXT NOT NULL,content_json TEXT NOT NULL,
 PRIMARY KEY(session_id,message_uuid,observation_id),FOREIGN KEY(session_id,message_uuid) REFERENCES messages(session_id,message_uuid));
