CREATE TABLE camera_devices (
    camera_device_id TEXT PRIMARY KEY NOT NULL,
    device_uid TEXT UNIQUE,
    device_name TEXT
);
