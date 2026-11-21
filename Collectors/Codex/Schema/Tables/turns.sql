CREATE TABLE turns (
    thread_id TEXT NOT NULL REFERENCES threads(thread_id), turn_id TEXT NOT NULL,
    started_at_utc_ms INTEGER,
    PRIMARY KEY(thread_id, turn_id)
);
