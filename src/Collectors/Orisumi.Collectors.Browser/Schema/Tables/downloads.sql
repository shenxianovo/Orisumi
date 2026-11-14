CREATE TABLE downloads (
 download_key INTEGER PRIMARY KEY,browser TEXT NOT NULL,profile_id TEXT NOT NULL,download_id INTEGER NOT NULL,
 UNIQUE(browser,profile_id,download_id));
