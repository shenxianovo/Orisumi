CREATE TABLE _source_cursors (
 source_id TEXT PRIMARY KEY NOT NULL, file_identity TEXT NOT NULL UNIQUE, source_path TEXT NOT NULL,
 byte_offset INTEGER NOT NULL, line_number INTEGER NOT NULL, observed_length INTEGER NOT NULL,
 modified_ticks INTEGER NOT NULL, prefix_hash TEXT NOT NULL, tail_hash TEXT NOT NULL,
 present INTEGER NOT NULL CHECK(present IN (0,1)));
