CREATE TABLE socket_state_observations (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    socket_id INTEGER NOT NULL REFERENCES socket_instances(socket_id),
    state TEXT,
    PRIMARY KEY(socket_id, sample_id)
);
