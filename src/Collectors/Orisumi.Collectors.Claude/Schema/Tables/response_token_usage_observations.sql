CREATE TABLE response_token_usage_observations (
 observation_id INTEGER NOT NULL REFERENCES source_records(observation_id),session_id TEXT NOT NULL REFERENCES sessions(session_id),response_id TEXT NOT NULL,
 recorded_at_utc_ms INTEGER NOT NULL,model TEXT NOT NULL,total_input_tokens INTEGER NOT NULL,
 cache_read_input_tokens INTEGER NOT NULL,cache_write_input_tokens INTEGER NOT NULL,total_output_tokens INTEGER NOT NULL,reasoning_output_tokens INTEGER,
 PRIMARY KEY(session_id,response_id,observation_id));
