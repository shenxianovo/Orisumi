CREATE TABLE _token_usage_rollouts (
    thread_id TEXT PRIMARY KEY NOT NULL REFERENCES threads(thread_id),
    path TEXT NOT NULL,
    length INTEGER NOT NULL,
    modified_ticks INTEGER NOT NULL
);
