CREATE VIEW response_token_usage AS SELECT o.* FROM response_token_usage_observations o
 WHERE observation_id=(SELECT MAX(n.observation_id) FROM response_token_usage_observations n WHERE (n.session_id,n.response_id)=(o.session_id,o.response_id));
