CREATE VIEW socket_history AS
SELECT e.*, s.run_id, s.run_sequence, s.observed_at_utc_ms, i.process_instance_id,
    i.protocol, i.ip_version, i.local_address, i.local_port, i.remote_address, i.remote_port,
    i.interface, i.ambiguous, p.pid, p.started_at_utc_us,
    (p.started_at_utc_us IS NOT NULL) AS process_identity_verified,
    (SELECT source_name FROM process_name_observations m WHERE m.process_instance_id=p.process_instance_id
        AND m.sample_id<=e.sample_id ORDER BY m.sample_id DESC LIMIT 1) AS source_name,
    (SELECT executable_path FROM process_path_observations m WHERE m.process_instance_id=p.process_instance_id
        AND m.sample_id<=e.sample_id ORDER BY m.sample_id DESC LIMIT 1) AS executable_path
FROM socket_presence_events e JOIN samples s USING(sample_id)
JOIN socket_instances i USING(socket_id) JOIN process_instances p USING(process_instance_id);
