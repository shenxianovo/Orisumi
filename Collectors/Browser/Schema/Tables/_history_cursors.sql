CREATE TABLE _history_cursors (
    profile_id TEXT PRIMARY KEY NOT NULL,
    initial_at_utc_ms INTEGER NOT NULL,
    forward_until_utc_ms INTEGER NOT NULL,
    backfill_before_utc_ms INTEGER NOT NULL
);
