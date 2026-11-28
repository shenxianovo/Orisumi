CREATE VIEW running_app_history AS
SELECT e.*, p.pid, p.created_at_filetime, p.executable_path, p.aumid, p.app_name, p.is_elevated
FROM running_app_observations e LEFT JOIN process_instances p USING (process_instance_id);
