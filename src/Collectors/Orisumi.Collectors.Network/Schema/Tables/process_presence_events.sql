CREATE TABLE process_presence_events (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    process_instance_id INTEGER NOT NULL REFERENCES process_instances(process_instance_id),
    present INTEGER NOT NULL CHECK(present IN (0,1)),
    PRIMARY KEY(process_instance_id, sample_id)
);
