CREATE TABLE process_name_observations (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    process_instance_id INTEGER NOT NULL REFERENCES process_instances(process_instance_id),
    source_name TEXT NOT NULL,
    PRIMARY KEY(process_instance_id, sample_id)
);
