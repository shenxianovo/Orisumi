CREATE TABLE source_file_events (
 event_id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),
 source_id TEXT NOT NULL, source_path TEXT NOT NULL, observed_at_utc_ms INTEGER NOT NULL,
 kind TEXT NOT NULL CHECK(kind IN ('initial','moved','missing','resumed','replaced','truncated','rewritten','rechecked')),
 file_identity TEXT, previous_offset INTEGER NOT NULL, observed_length INTEGER);
