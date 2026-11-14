CREATE TABLE socket_traffic_observations (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    socket_id INTEGER NOT NULL REFERENCES socket_instances(socket_id),
    direction TEXT NOT NULL CHECK(direction IN ('received','sent')),
    bytes INTEGER CHECK(bytes >= 0),
    PRIMARY KEY(socket_id, direction, sample_id)
);
