CREATE VIEW response_token_usage AS SELECT o.* FROM response_token_usage_observations o
WHERE observation_id=(SELECT MAX(n.observation_id) FROM response_token_usage_observations n WHERE n.thread_id=o.thread_id AND n.response_id=o.response_id);
