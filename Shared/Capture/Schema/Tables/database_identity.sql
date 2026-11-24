CREATE TABLE database_identity (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    database_id TEXT NOT NULL CHECK (length(database_id) = 32),
    created_at_utc_ms INTEGER NOT NULL,
    platform TEXT NOT NULL CHECK (platform IN ('{platform}')),
    hardware_uuid TEXT,
    machine_guid TEXT
);
