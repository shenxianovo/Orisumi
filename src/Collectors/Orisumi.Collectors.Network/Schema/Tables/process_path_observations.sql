CREATE TABLE process_path_observations (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    process_instance_id INTEGER NOT NULL REFERENCES process_instances(process_instance_id),
    executable_path TEXT,
    PRIMARY KEY(process_instance_id, sample_id)
);
