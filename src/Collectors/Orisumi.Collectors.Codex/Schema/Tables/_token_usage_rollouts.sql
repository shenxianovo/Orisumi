CREATE TABLE _token_usage_rollouts (
    thread_id TEXT PRIMARY KEY NOT NULL REFERENCES threads(thread_id),
    path TEXT NOT NULL,
    file_id TEXT NOT NULL,
    length INTEGER NOT NULL,
    modified_ticks INTEGER NOT NULL,
    byte_offset INTEGER NOT NULL,
    line_number INTEGER NOT NULL,
    prefix_hash TEXT NOT NULL,
    tail_hash TEXT NOT NULL,
    models_json TEXT NOT NULL
);
