CREATE TABLE socket_instances (
    socket_id INTEGER PRIMARY KEY,
    process_instance_id INTEGER NOT NULL REFERENCES process_instances(process_instance_id),
    protocol TEXT NOT NULL CHECK(protocol IN ('tcp','udp')),
    ip_version INTEGER NOT NULL CHECK(ip_version IN (4,6)),
    local_address TEXT NOT NULL, local_port INTEGER,
    remote_address TEXT NOT NULL, remote_port INTEGER,
    interface TEXT,
    ambiguous INTEGER NOT NULL CHECK(ambiguous IN (0,1))
);
