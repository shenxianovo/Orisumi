CREATE VIEW foreground_app_history AS SELECT e.*,p.pid,p.bundle_id,p.app_name
FROM foreground_app_events e LEFT JOIN process_instances p USING(process_instance_id);
