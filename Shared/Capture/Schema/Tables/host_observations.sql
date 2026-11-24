CREATE TABLE host_observations (
    observation_id INTEGER PRIMARY KEY,
    observed_at_utc_ms INTEGER NOT NULL,
    platform TEXT NOT NULL CHECK (platform IN ('{platform}')),
    hardware_uuid TEXT,
    host_name TEXT,
    model TEXT,
    os_version TEXT,
    os_build TEXT,
    boot_id TEXT,
    user_id INTEGER,
    user_name TEXT,
    store_path TEXT NOT NULL,
    machine_guid TEXT,
    user_sid TEXT
);
