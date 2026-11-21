CREATE VIEW running_app_history AS SELECT e.*,p.pid,p.bundle_id,p.app_name,p.app_launched_at_utc_ms
FROM running_app_observations e JOIN process_instances p USING(process_instance_id);
