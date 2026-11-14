CREATE TABLE _collector_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    database_id TEXT NOT NULL
);
