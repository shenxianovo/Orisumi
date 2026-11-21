CREATE TABLE _rollout_cursors (
    rollout_id INTEGER PRIMARY KEY REFERENCES rollouts(rollout_id),
    thread_id TEXT REFERENCES threads(thread_id),
    source_file_event_id INTEGER NOT NULL REFERENCES source_file_events(event_id),
    read_version_id INTEGER NOT NULL REFERENCES source_file_events(event_id),
    byte_offset INTEGER NOT NULL,
    line_number INTEGER NOT NULL,
    observed_length INTEGER NOT NULL,
    modified_ticks INTEGER NOT NULL,
    prefix_hash TEXT NOT NULL,
    tail_hash TEXT NOT NULL,
    committed_hash TEXT,
    present INTEGER NOT NULL CHECK(present IN (0,1))
);
