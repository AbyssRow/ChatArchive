BEGIN;
CREATE TABLE IF NOT EXISTS weflow_messages (
  session_id TEXT NOT NULL,
  local_id TEXT,
  message_id TEXT,
  create_time BIGINT NOT NULL,
  sender TEXT,
  is_send BOOLEAN NOT NULL,
  local_type INTEGER,
  media_type TEXT,
  content TEXT,
  media_path TEXT
);
CREATE INDEX IF NOT EXISTS idx_weflow_messages_session_time ON weflow_messages (session_id, create_time);
INSERT INTO weflow_messages (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path) VALUES ('fixture-weflow-sql', '12', 'fixture-message-weflow-sql', 1701000012, 'fixture-sender-weflow-sql', FALSE, 1, NULL, '你好，WeFlow SQL', NULL);
COMMIT;
