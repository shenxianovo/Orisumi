CREATE TABLE response_token_usage_observations (
    observation_id INTEGER PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
    source_line INTEGER NOT NULL,
    thread_id TEXT NOT NULL REFERENCES threads(thread_id),
    response_id TEXT NOT NULL,
    turn_id TEXT,
    recorded_at_utc_ms INTEGER NOT NULL,
    model TEXT,
    total_input_tokens INTEGER NOT NULL,
    cache_read_input_tokens INTEGER,
    cache_write_input_tokens INTEGER,
    total_output_tokens INTEGER NOT NULL,
    reasoning_output_tokens INTEGER,
    UNIQUE (thread_id, source_line)
);
