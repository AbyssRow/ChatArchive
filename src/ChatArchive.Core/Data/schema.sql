PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS app_metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT INTO app_metadata(key, value) VALUES ('schema_version', '2')
ON CONFLICT(key) DO NOTHING;

CREATE TABLE IF NOT EXISTS import_runs (
    id INTEGER PRIMARY KEY,
    root_paths_json TEXT NOT NULL,
    started_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at TEXT,
    status TEXT NOT NULL DEFAULT 'running',
    stats_json TEXT NOT NULL DEFAULT '{}',
    error TEXT
);

CREATE TABLE IF NOT EXISTS import_files (
    id INTEGER PRIMARY KEY,
    import_run_id INTEGER REFERENCES import_runs(id) ON DELETE SET NULL,
    platform TEXT,
    source_path TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    file_size INTEGER NOT NULL,
    modified_at_ns INTEGER,
    status TEXT NOT NULL,
    started_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at TEXT,
    stats_json TEXT NOT NULL DEFAULT '{}',
    error TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_import_files_completed_hash
ON import_files(sha256) WHERE status = 'completed';

CREATE INDEX IF NOT EXISTS ix_import_files_run ON import_files(import_run_id);

CREATE TABLE IF NOT EXISTS conversations (
    id INTEGER PRIMARY KEY,
    platform TEXT NOT NULL CHECK(platform IN ('qq', 'wechat')),
    account_id TEXT NOT NULL,
    native_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('private', 'group')),
    title TEXT NOT NULL,
    first_message_at INTEGER,
    last_message_at INTEGER,
    message_count INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(platform, account_id, native_id)
);

CREATE INDEX IF NOT EXISTS ix_conversations_last_message
ON conversations(last_message_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS conversation_aliases (
    id INTEGER PRIMARY KEY,
    conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    alias TEXT NOT NULL,
    first_seen_at INTEGER,
    last_seen_at INTEGER,
    UNIQUE(conversation_id, alias)
);

CREATE TABLE IF NOT EXISTS senders (
    id INTEGER PRIMARY KEY,
    platform TEXT NOT NULL CHECK(platform IN ('qq', 'wechat')),
    account_id TEXT NOT NULL,
    native_id TEXT NOT NULL,
    current_name TEXT NOT NULL,
    is_self INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(platform, account_id, native_id)
);

CREATE TABLE IF NOT EXISTS sender_aliases (
    id INTEGER PRIMARY KEY,
    sender_id INTEGER NOT NULL REFERENCES senders(id) ON DELETE CASCADE,
    conversation_id INTEGER REFERENCES conversations(id) ON DELETE CASCADE,
    alias TEXT NOT NULL,
    first_seen_at INTEGER,
    last_seen_at INTEGER,
    UNIQUE(sender_id, conversation_id, alias)
);

CREATE TABLE IF NOT EXISTS messages (
    id INTEGER PRIMARY KEY,
    conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    sender_id INTEGER REFERENCES senders(id) ON DELETE SET NULL,
    platform TEXT NOT NULL CHECK(platform IN ('qq', 'wechat')),
    native_id TEXT,
    local_id TEXT,
    timestamp_ms INTEGER NOT NULL,
    sequence TEXT,
    direction TEXT NOT NULL CHECK(direction IN ('incoming', 'outgoing', 'system')),
    message_type TEXT NOT NULL,
    media_type TEXT,
    content TEXT NOT NULL,
    search_text TEXT NOT NULL,
    sender_name_snapshot TEXT NOT NULL,
    conversation_title_snapshot TEXT NOT NULL,
    is_recalled INTEGER NOT NULL DEFAULT 0,
    is_system INTEGER NOT NULL DEFAULT 0,
    reply_to_native_id TEXT,
    payload_hash TEXT NOT NULL,
    semantic_hash TEXT NOT NULL,
    revision_of_id INTEGER REFERENCES messages(id) ON DELETE SET NULL,
    raw_payload_json TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_native_payload
ON messages(conversation_id, native_id, payload_hash)
WHERE native_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_fallback_payload
ON messages(conversation_id, semantic_hash, payload_hash)
WHERE native_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_messages_conversation_time
ON messages(conversation_id, timestamp_ms DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_messages_native
ON messages(conversation_id, native_id) WHERE native_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_messages_sender ON messages(sender_id);
CREATE INDEX IF NOT EXISTS ix_messages_type ON messages(message_type);
CREATE INDEX IF NOT EXISTS ix_messages_timestamp ON messages(timestamp_ms DESC, id DESC);

CREATE TABLE IF NOT EXISTS message_observations (
    id INTEGER PRIMARY KEY,
    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    import_file_id INTEGER NOT NULL REFERENCES import_files(id) ON DELETE CASCADE,
    source_locator TEXT NOT NULL,
    observed_payload_hash TEXT NOT NULL,
    observed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(message_id, import_file_id, source_locator)
);

CREATE INDEX IF NOT EXISTS ix_observations_file ON message_observations(import_file_id);

CREATE TABLE IF NOT EXISTS media_objects (
    id INTEGER PRIMARY KEY,
    sha256 TEXT NOT NULL UNIQUE,
    size INTEGER NOT NULL,
    mime_type TEXT,
    managed_path TEXT,
    first_source_path TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS attachments (
    id INTEGER PRIMARY KEY,
    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    kind TEXT NOT NULL,
    filename TEXT,
    declared_path TEXT,
    source_path TEXT,
    is_available INTEGER NOT NULL DEFAULT 0,
    media_object_id INTEGER REFERENCES media_objects(id) ON DELETE SET NULL,
    declared_size INTEGER,
    mime_type TEXT,
    width INTEGER,
    height INTEGER,
    duration REAL,
    metadata_json TEXT NOT NULL DEFAULT '{}',
    UNIQUE(message_id, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_attachments_media_object ON attachments(media_object_id);

CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
    content,
    search_text,
    sender_name_snapshot,
    conversation_title_snapshot,
    content='messages',
    content_rowid='id',
    tokenize='trigram'
);

CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(
        rowid, content, search_text, sender_name_snapshot, conversation_title_snapshot
    ) VALUES (
        new.id, new.content, new.search_text,
        new.sender_name_snapshot, new.conversation_title_snapshot
    );
END;

CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
    INSERT INTO messages_fts(
        messages_fts, rowid, content, search_text,
        sender_name_snapshot, conversation_title_snapshot
    ) VALUES (
        'delete', old.id, old.content, old.search_text,
        old.sender_name_snapshot, old.conversation_title_snapshot
    );
END;

CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
    INSERT INTO messages_fts(
        messages_fts, rowid, content, search_text,
        sender_name_snapshot, conversation_title_snapshot
    ) VALUES (
        'delete', old.id, old.content, old.search_text,
        old.sender_name_snapshot, old.conversation_title_snapshot
    );
    INSERT INTO messages_fts(
        rowid, content, search_text, sender_name_snapshot, conversation_title_snapshot
    ) VALUES (
        new.id, new.content, new.search_text,
        new.sender_name_snapshot, new.conversation_title_snapshot
    );
END;

CREATE TABLE IF NOT EXISTS contacts (
    id INTEGER PRIMARY KEY,
    display_name TEXT NOT NULL,
    custom_avatar_path TEXT,
    note TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_contacts_display_name ON contacts(display_name);

CREATE TABLE IF NOT EXISTS contact_senders (
    contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL REFERENCES senders(id) ON DELETE CASCADE,
    account_label TEXT,
    is_primary INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (contact_id, sender_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_contact_senders_sender ON contact_senders(sender_id);
CREATE INDEX IF NOT EXISTS ix_contact_senders_contact ON contact_senders(contact_id);

