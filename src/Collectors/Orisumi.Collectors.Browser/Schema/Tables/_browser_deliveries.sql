CREATE TABLE _browser_deliveries (
    profile_id TEXT NOT NULL,
    queue_id TEXT NOT NULL,
    sequence INTEGER NOT NULL CHECK(sequence > 0),
    PRIMARY KEY(profile_id, queue_id)
);
