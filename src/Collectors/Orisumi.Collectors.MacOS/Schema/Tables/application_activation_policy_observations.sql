CREATE TABLE application_activation_policy_observations (
    run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
    run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,
    process_instance_id TEXT NOT NULL REFERENCES process_instances(process_instance_id),
    activation_policy TEXT CHECK (activation_policy IN ('regular', 'accessory', 'prohibited')),
    PRIMARY KEY (run_id, run_sequence)
);
