CREATE TABLE capture_runs (
 run_id TEXT PRIMARY KEY NOT NULL,browser TEXT NOT NULL,profile_id TEXT NOT NULL,
 scope TEXT NOT NULL CHECK(scope IN ('bookmarks','tabs','downloads')),
 started_at_utc_ms INTEGER NOT NULL,stopped_at_utc_ms INTEGER,
 host_observation_id INTEGER REFERENCES host_observations(observation_id));
