CREATE INDEX rollout_events_by_turn ON rollout_events(thread_id, turn_id, recorded_at_utc_ms, event_id);
