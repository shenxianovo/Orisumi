CREATE TABLE process_traffic_observations (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    process_instance_id INTEGER NOT NULL REFERENCES process_instances(process_instance_id),
    direction TEXT NOT NULL CHECK(direction IN ('received','sent')),
    bytes INTEGER CHECK(bytes >= 0),
    PRIMARY KEY(process_instance_id, direction, sample_id)
);
