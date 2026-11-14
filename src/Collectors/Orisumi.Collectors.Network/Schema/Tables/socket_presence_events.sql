CREATE TABLE socket_presence_events (
    sample_id INTEGER NOT NULL REFERENCES samples(sample_id),
    socket_id INTEGER NOT NULL REFERENCES socket_instances(socket_id),
    present INTEGER NOT NULL CHECK(present IN (0,1)),
    PRIMARY KEY(socket_id, sample_id)
);
