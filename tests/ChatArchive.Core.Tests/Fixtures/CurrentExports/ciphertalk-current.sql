-- ============================================================
-- 密语 CipherTalk - 聊天记录导出
-- 生成时间: 2023-11-26 12:00:13
-- 会话: CipherTalk SQL
-- 类型: 私聊
-- 消息数: 1
-- PostgreSQL 兼容 SQL 脚本
-- ============================================================

DELETE FROM messages WHERE session_wxid = 'fixture-ciphertalk-sql';
DELETE FROM sessions WHERE wxid = 'fixture-ciphertalk-sql';

CREATE TABLE IF NOT EXISTS sessions (
  wxid TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  session_type TEXT NOT NULL,
  owner_id TEXT,
  message_count INTEGER DEFAULT 0,
  first_message_time BIGINT,
  last_message_time BIGINT,
  exported_at BIGINT
);

CREATE TABLE IF NOT EXISTS messages (
  id SERIAL PRIMARY KEY,
  session_wxid TEXT NOT NULL REFERENCES sessions(wxid),
  local_id INTEGER,
  create_time BIGINT NOT NULL,
  formatted_time TEXT,
  msg_type TEXT,
  content TEXT,
  is_send SMALLINT DEFAULT 0,
  sender_username TEXT,
  sender_display_name TEXT,
  group_nickname TEXT,
  reply_to_message_id TEXT
);

CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_wxid);
CREATE INDEX IF NOT EXISTS idx_messages_create_time ON messages(create_time);
CREATE INDEX IF NOT EXISTS idx_messages_sender ON messages(sender_username);

INSERT INTO sessions (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at) VALUES ('fixture-ciphertalk-sql', 'CipherTalk SQL', 'private', 'fixture-owner-ciphertalk-sql', 1, 1701000013, 1701000013, 1701000013);

INSERT INTO messages (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id) VALUES
('fixture-ciphertalk-sql', 13, 1701000013, '2023-11-26 12:00:13', '文本消息', '你好，CipherTalk SQL', 0, 'fixture-sender-ciphertalk-sql', 'CipherTalk SQL 发送者', NULL, NULL);

-- 导出完成
-- 会话: CipherTalk SQL | 1 条消息
