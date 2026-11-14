CREATE VIEW sample_windows AS
SELECT s.*,
    MAX(CASE WHEN continuous=0 THEN sample_id END)
        OVER (PARTITION BY run_id ORDER BY run_sequence) AS coverage_start_sample_id
FROM samples s;
