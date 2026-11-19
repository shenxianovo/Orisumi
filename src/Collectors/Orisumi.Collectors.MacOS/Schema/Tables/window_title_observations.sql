CREATE TABLE window_title_observations (
    run_id INTEGER NOT NULL REFERENCES capture_runs(run_id),run_sequence INTEGER NOT NULL,
    observed_at_utc_ms INTEGER NOT NULL,focus_id TEXT NOT NULL REFERENCES window_focus_instances(focus_id),
    title TEXT, -- '' when the window has no title; NULL when it could not be read.
    PRIMARY KEY(run_id,run_sequence)
);
