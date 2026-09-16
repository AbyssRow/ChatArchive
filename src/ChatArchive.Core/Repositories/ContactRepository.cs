using System.Globalization;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Repositories;

public sealed class ContactRepository
{
    private readonly ArchiveDatabase _db;

    public ContactRepository(ArchiveDatabase db)
    {
        _db = db;
    }

    public long CreateContact(
        string displayName,
        string? customAvatarPath = null,
        string? note = null,
        IEnumerable<(long SenderId, string? Label, bool IsPrimary)>? initialBindings = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("联系人姓名不能为空", nameof(displayName));
        }

        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long contactId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO contacts(display_name, custom_avatar_path, note, created_at, updated_at)
                VALUES (@name, @avatar, @note, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@name", displayName.Trim());
            cmd.Parameters.AddWithValue("@avatar", (object?)customAvatarPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            contactId = Convert.ToInt64(cmd.ExecuteScalar());
        }

        if (initialBindings != null)
        {
            foreach (var (senderId, label, isPrimary) in initialBindings)
            {
                BindSenderInternal(connection, transaction, contactId, senderId, label, isPrimary, forceRebind: false);
            }
        }

        transaction.Commit();
        return contactId;
    }

    public void UpdateContact(long contactId, string displayName, string? customAvatarPath, string? note)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("联系人姓名不能为空", nameof(displayName));
        }

        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE contacts
            SET display_name = @name,
                custom_avatar_path = @avatar,
                note = @note,
                updated_at = CURRENT_TIMESTAMP
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", contactId);
        cmd.Parameters.AddWithValue("@name", displayName.Trim());
        cmd.Parameters.AddWithValue("@avatar", (object?)customAvatarPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"未找到 ID 为 {contactId} 的联系人");
        }
    }

    public void DeleteContact(long contactId)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM contacts WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", contactId);
        cmd.ExecuteNonQuery();
    }

    public void BindSender(
        long contactId,
        long senderId,
        string? accountLabel = null,
        bool isPrimary = false,
        bool forceRebind = false)
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BindSenderInternal(connection, transaction, contactId, senderId, accountLabel, isPrimary, forceRebind);
        transaction.Commit();
    }

    public void BindSenderToExpectedContact(
        long targetContactId,
        string expectedTargetIdentityToken,
        long senderId,
        string? accountLabel = null,
        bool isPrimary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTargetIdentityToken);

        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BindSenderInternal(
            connection,
            transaction,
            targetContactId,
            senderId,
            accountLabel,
            isPrimary,
            forceRebind: false,
            expectedTargetIdentityToken: expectedTargetIdentityToken,
            requireUnboundSender: true);
        transaction.Commit();
    }

    public void TransferSenderFromExpectedContact(
        long targetContactId,
        string expectedTargetIdentityToken,
        long senderId,
        long expectedSourceContactId,
        string expectedSourceIdentityToken,
        string? accountLabel = null,
        bool isPrimary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTargetIdentityToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceIdentityToken);

        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        BindSenderInternal(
            connection,
            transaction,
            targetContactId,
            senderId,
            accountLabel,
            isPrimary,
            forceRebind: false,
            expectedSourceContactId,
            expectedTargetIdentityToken,
            expectedSourceIdentityToken);
        transaction.Commit();
    }

    public void UnbindSender(long contactId, long senderId)
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM contact_senders WHERE contact_id = @cid AND sender_id = @sid;";
            cmd.Parameters.AddWithValue("@cid", contactId);
            cmd.Parameters.AddWithValue("@sid", senderId);
            cmd.ExecuteNonQuery();
        }

        AfterUnbind(connection, transaction, contactId);
        transaction.Commit();
    }

    public void UpdateAccountLabel(long contactId, long senderId, string? newLabel)
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE contact_senders
                SET account_label = @label
                WHERE contact_id = @cid AND sender_id = @sid;
                """;
            cmd.Parameters.AddWithValue("@cid", contactId);
            cmd.Parameters.AddWithValue("@sid", senderId);
            cmd.Parameters.AddWithValue("@label", (object?)newLabel ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        Touch(connection, transaction, contactId);
        transaction.Commit();
    }

    public void SetPrimarySender(long contactId, long senderId)
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        ClearPrimary(connection, transaction, contactId);

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE contact_senders SET is_primary = 1 WHERE contact_id = @cid AND sender_id = @sid;";
            cmd.Parameters.AddWithValue("@cid", contactId);
            cmd.Parameters.AddWithValue("@sid", senderId);
            cmd.ExecuteNonQuery();
        }

        Touch(connection, transaction, contactId);
        transaction.Commit();
    }

    public ContactDetail? GetContactDetail(long contactId)
    {
        using var connection = _db.OpenConnection();

        string displayName;
        string? customAvatarPath;
        string? note;
        string identityToken;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, display_name, custom_avatar_path, note, identity_token FROM contacts WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", contactId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            displayName = reader.GetString(1);
            customAvatarPath = reader.IsDBNull(2) ? null : reader.GetString(2);
            note = reader.IsDBNull(3) ? null : reader.GetString(3);
            identityToken = reader.GetString(4);
        }

        var senderRawList = new List<SenderRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.id, s.platform, s.native_id, s.current_name, cs.account_label, cs.is_primary,
                       (SELECT COUNT(*) FROM messages m WHERE m.sender_id = s.id) AS msg_count
                FROM contact_senders cs
                JOIN senders s ON s.id = cs.sender_id
                WHERE cs.contact_id = @id
                ORDER BY cs.is_primary DESC, s.id ASC;
                """;
            cmd.Parameters.AddWithValue("@id", contactId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                senderRawList.Add(new SenderRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5) != 0,
                    reader.GetInt64(6)));
            }
        }

        var boundSenders = MapSenders(connection, senderRawList);
        var conversations = LoadConversationsForContact(connection, contactId, displayName);

        return new ContactDetail(
            contactId,
            displayName,
            customAvatarPath,
            note,
            boundSenders,
            conversations,
            boundSenders.Sum(s => s.MessageCount)
        ) { IdentityToken = identityToken };
    }

    public ContactInfo? FindContactBySenderId(long senderId)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.display_name, c.custom_avatar_path, c.note,
                   (SELECT COUNT(*) FROM messages m
                    JOIN contact_senders cs2 ON cs2.sender_id = m.sender_id
                    WHERE cs2.contact_id = c.id) AS total_messages,
                   c.created_at, c.updated_at, c.identity_token
            FROM contacts c
            JOIN contact_senders cs ON cs.contact_id = c.id
            WHERE cs.sender_id = @sid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@sid", senderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ContactInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            ParseTimestampMs(reader.GetValue(5)),
            ParseTimestampMs(reader.GetValue(6))
        ) { IdentityToken = reader.GetString(7) };
    }

    public IReadOnlyList<ContactInfo> ListContacts(string? keyword = null)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();

        var whereClause = "";
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            whereClause = """
                WHERE (
                    c.display_name LIKE @query ESCAPE '/'
                    OR c.note LIKE @query ESCAPE '/'
                    OR EXISTS (
                        SELECT 1 FROM contact_senders cs
                        JOIN senders s ON s.id = cs.sender_id
                        WHERE cs.contact_id = c.id
                          AND (
                              s.current_name LIKE @query ESCAPE '/'
                              OR s.native_id LIKE @query ESCAPE '/'
                              OR cs.account_label LIKE @query ESCAPE '/'
                              OR EXISTS (
                                  SELECT 1 FROM sender_aliases sa
                                  WHERE sa.sender_id = s.id AND sa.alias LIKE @query ESCAPE '/'
                              )
                          )
                    )
                )
                """;
            cmd.Parameters.AddWithValue("@query", $"%{SqliteLikeHelper.EscapeLikePattern(keyword.Trim())}%");
        }

        cmd.CommandText = $"""
            SELECT c.id, c.display_name, c.custom_avatar_path, c.note,
                   (SELECT COUNT(*) FROM messages m
                    JOIN contact_senders cs ON cs.sender_id = m.sender_id
                    WHERE cs.contact_id = c.id) AS total_messages,
                   c.created_at, c.updated_at, c.identity_token
            FROM contacts c
            {whereClause}
            ORDER BY c.updated_at DESC, c.id DESC;
            """;

        using var reader = cmd.ExecuteReader();
        var list = new List<ContactInfo>();
        while (reader.Read())
        {
            list.Add(new ContactInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                ParseTimestampMs(reader.GetValue(5)),
                ParseTimestampMs(reader.GetValue(6))
            ) { IdentityToken = reader.GetString(7) });
        }

        return list;
    }

    public IReadOnlyList<BoundSenderInfo> ListAvailableSendersToBind(long currentContactId, string? keyword = null)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();

        var where = new List<string>
        {
            "s.is_self = 0",
            "NOT EXISTS (SELECT 1 FROM contact_senders cs WHERE cs.sender_id = s.id AND cs.contact_id = @currentCid)"
        };
        cmd.Parameters.AddWithValue("@currentCid", currentContactId);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("""
                (
                    s.current_name LIKE @query ESCAPE '/'
                    OR s.native_id LIKE @query ESCAPE '/'
                    OR c.display_name LIKE @query ESCAPE '/'
                    OR cs.account_label LIKE @query ESCAPE '/'
                    OR EXISTS (
                        SELECT 1 FROM sender_aliases sa
                        WHERE sa.sender_id = s.id AND sa.alias LIKE @query ESCAPE '/'
                    )
                )
                """);
            cmd.Parameters.AddWithValue("@query", $"%{SqliteLikeHelper.EscapeLikePattern(keyword.Trim())}%");
        }

        cmd.CommandText = $"""
            SELECT s.id, s.platform, s.native_id, s.current_name,
                   c.display_name AS bound_contact_name, c.id AS bound_contact_id,
                   c.identity_token AS bound_contact_identity_token, cs.account_label,
                   (SELECT COUNT(*) FROM messages m WHERE m.sender_id = s.id) AS msg_count
            FROM senders s
            LEFT JOIN contact_senders cs ON cs.sender_id = s.id
            LEFT JOIN contacts c ON c.id = cs.contact_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY msg_count DESC, s.id DESC;
            """;

        var rawList = new List<SenderRow>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rawList.Add(new SenderRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsPrimary: false,
                    reader.GetInt64(8),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        return MapSenders(connection, rawList);
    }

    public int AutoPopulateContactsFromSenders()
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandText = """
            SELECT DISTINCT s.id, s.current_name, s.native_id, s.platform,
                   (SELECT c.title FROM conversations c
                    JOIN messages m ON m.conversation_id = c.id
                    WHERE m.sender_id = s.id AND c.kind = 'private' AND c.title != ''
                    ORDER BY c.last_message_at DESC LIMIT 1)
            FROM senders s
            WHERE s.is_self = 0
              AND NOT EXISTS (SELECT 1 FROM contact_senders cs WHERE cs.sender_id = s.id)
              AND (
                  EXISTS (
                      SELECT 1 FROM messages m
                      JOIN conversations c ON c.id = m.conversation_id
                      WHERE m.sender_id = s.id AND c.kind = 'private'
                  )
                  OR EXISTS (
                      SELECT 1 FROM conversations c
                      WHERE c.platform = s.platform AND c.native_id = s.native_id AND c.kind = 'private'
                  )
              )
            ORDER BY s.id;
            """;

        var unbound = new List<(long SenderId, string CurrentName, string NativeId, string Platform, string? PrivateTitle)>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                unbound.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        if (unbound.Count == 0)
        {
            return 0;
        }

        var resolved = SenderDisplayName.Resolve(
            connection,
            unbound.Select(u => (SenderId: u.SenderId, ConversationId: (long?)null)));

        var count = 0;
        foreach (var item in unbound)
        {
            var rawName = !string.IsNullOrWhiteSpace(item.PrivateTitle)
                ? item.PrivateTitle.Trim()
                : (resolved.TryGetValue((item.SenderId, null), out var rName) && !string.IsNullOrWhiteSpace(rName)
                    ? rName
                    : item.CurrentName);

            var name = string.IsNullOrWhiteSpace(rawName)
                ? (item.Platform == "qq" ? $"QQ_{item.NativeId}" : $"微信_{item.NativeId}")
                : rawName.Trim();

            using var insertContactCmd = connection.CreateCommand();
            insertContactCmd.Transaction = transaction;
            insertContactCmd.CommandText = """
                INSERT INTO contacts(display_name, created_at, updated_at)
                VALUES (@name, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """;
            insertContactCmd.Parameters.AddWithValue("@name", name);
            var contactId = Convert.ToInt64(insertContactCmd.ExecuteScalar());

            using var insertBindingCmd = connection.CreateCommand();
            insertBindingCmd.Transaction = transaction;
            insertBindingCmd.CommandText = """
                INSERT INTO contact_senders(contact_id, sender_id, is_primary, created_at)
                VALUES (@contactId, @senderId, 1, CURRENT_TIMESTAMP);
                """;
            insertBindingCmd.Parameters.AddWithValue("@contactId", contactId);
            insertBindingCmd.Parameters.AddWithValue("@senderId", item.SenderId);
            insertBindingCmd.ExecuteNonQuery();
            count++;
        }

        transaction.Commit();
        return count;
    }

    private readonly record struct SenderRow(
        long SenderId,
        string Platform,
        string NativeId,
        string CurrentName,
        string? AccountLabel,
        bool IsPrimary,
        long MessageCount,
        string? BoundContactName = null,
        long? BoundContactId = null,
        string? BoundContactIdentityToken = null);

    private static List<BoundSenderInfo> MapSenders(SqliteConnection connection, IReadOnlyList<SenderRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var aliasesBatch = LoadAliasesBatch(connection, rows.Select(r => r.SenderId).Distinct().ToList());
        var resolvedNames = SenderDisplayName.Resolve(
            connection,
            rows.Select(r => (SenderId: r.SenderId, ConversationId: (long?)null)));

        var result = new List<BoundSenderInfo>(rows.Count);
        foreach (var raw in rows)
        {
            aliasesBatch.TryGetValue(raw.SenderId, out var aliases);
            aliases ??= [];

            var originalName = resolvedNames.TryGetValue((raw.SenderId, null), out var dn)
                ? dn
                : raw.CurrentName;

            result.Add(new BoundSenderInfo(
                raw.SenderId,
                raw.Platform,
                raw.NativeId,
                raw.Platform == "qq" ? SenderRepository.FindQqNumber(connection, raw.SenderId, aliases) : null,
                originalName,
                raw.AccountLabel,
                raw.IsPrimary,
                raw.MessageCount,
                raw.BoundContactName)
            {
                BoundContactId = raw.BoundContactId,
                BoundContactIdentityToken = raw.BoundContactIdentityToken,
            });
        }

        return result;
    }

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, long contactId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE contacts SET updated_at = CURRENT_TIMESTAMP WHERE id = @cid;";
        command.Parameters.AddWithValue("@cid", contactId);
        command.ExecuteNonQuery();
    }

    private static void DeleteIfEmpty(SqliteConnection connection, SqliteTransaction transaction, long contactId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM contacts
            WHERE id = @cid
              AND NOT EXISTS (SELECT 1 FROM contact_senders WHERE contact_id = @cid)
              AND (note IS NULL OR note = '')
              AND (custom_avatar_path IS NULL OR custom_avatar_path = '');
            """;
        command.Parameters.AddWithValue("@cid", contactId);
        command.ExecuteNonQuery();
    }

    private static void ClearPrimary(SqliteConnection connection, SqliteTransaction transaction, long contactId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE contact_senders SET is_primary = 0 WHERE contact_id = @cid;";
        command.Parameters.AddWithValue("@cid", contactId);
        command.ExecuteNonQuery();
    }

    private static void AfterUnbind(SqliteConnection connection, SqliteTransaction transaction, long contactId)
    {
        PromotePrimarySenderIfNeeded(connection, transaction, contactId);
        DeleteIfEmpty(connection, transaction, contactId);
        Touch(connection, transaction, contactId);
    }

    private static void PromotePrimarySenderIfNeeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long contactId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE contact_senders
            SET is_primary = 1
            WHERE contact_id = @cid AND rowid = (
                SELECT rowid FROM contact_senders WHERE contact_id = @cid ORDER BY sender_id ASC LIMIT 1
            ) AND NOT EXISTS (SELECT 1 FROM contact_senders WHERE contact_id = @cid AND is_primary = 1);
            """;
        command.Parameters.AddWithValue("@cid", contactId);
        command.ExecuteNonQuery();
    }

    private static void BindSenderInternal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long contactId,
        long senderId,
        string? accountLabel,
        bool isPrimary,
        bool forceRebind,
        long? expectedSourceContactId = null,
        string? expectedTargetIdentityToken = null,
        string? expectedSourceIdentityToken = null,
        bool requireUnboundSender = false)
    {
        string targetIdentityToken;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT identity_token FROM contacts WHERE id = @cid;";
            cmd.Parameters.AddWithValue("@cid", contactId);
            var result = cmd.ExecuteScalar();
            if (result is not string token)
            {
                throw new KeyNotFoundException($"未找到 ID 为 {contactId} 的联系人");
            }

            targetIdentityToken = token;
        }

        if (expectedTargetIdentityToken is not null
            && !string.Equals(targetIdentityToken, expectedTargetIdentityToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("目标联系人已发生变化，请重新选择联系人后重试");
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(*) FROM senders WHERE id = @sid;";
            cmd.Parameters.AddWithValue("@sid", senderId);
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            {
                throw new KeyNotFoundException($"未找到 ID 为 {senderId} 的账号");
            }
        }

        long? existingContactId = null;
        string? existingContactIdentityToken = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                SELECT cs.contact_id, c.identity_token
                FROM contact_senders cs
                JOIN contacts c ON c.id = cs.contact_id
                WHERE cs.sender_id = @sid;
                """;
            cmd.Parameters.AddWithValue("@sid", senderId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                existingContactId = reader.GetInt64(0);
                existingContactIdentityToken = reader.GetString(1);
            }
        }

        if (requireUnboundSender && existingContactId.HasValue)
        {
            throw new InvalidOperationException("账号归属已发生变化，请重新选择账号后重试");
        }

        if (expectedSourceContactId.HasValue)
        {
            if (!existingContactId.HasValue
                || existingContactId.Value != expectedSourceContactId.Value
                || existingContactId.Value == contactId
                || (expectedSourceIdentityToken is not null
                    && !string.Equals(
                        existingContactIdentityToken,
                        expectedSourceIdentityToken,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("来源联系人或账号归属已发生变化，请重新选择账号后重试");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    DELETE FROM contact_senders
                    WHERE sender_id = @sid
                      AND contact_id = @expectedCid
                      AND (
                          @expectedSourceIdentityToken IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM contacts c
                              WHERE c.id = @expectedCid
                                AND c.identity_token = @expectedSourceIdentityToken
                          )
                      );
                    """;
                cmd.Parameters.AddWithValue("@sid", senderId);
                cmd.Parameters.AddWithValue("@expectedCid", expectedSourceContactId.Value);
                cmd.Parameters.AddWithValue(
                    "@expectedSourceIdentityToken",
                    (object?)expectedSourceIdentityToken ?? DBNull.Value);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException("账号归属已发生变化，请重新选择账号后重试");
                }
            }

            AfterUnbind(connection, transaction, expectedSourceContactId.Value);
            existingContactId = null;
        }

        if (existingContactId.HasValue)
        {
            if (existingContactId.Value != contactId)
            {
                if (!forceRebind)
                {
                    throw new InvalidOperationException($"账号 (ID: {senderId}) 已绑定到联系人 (ID: {existingContactId.Value})，若需转移请指定 forceRebind = true");
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM contact_senders WHERE sender_id = @sid;";
                    cmd.Parameters.AddWithValue("@sid", senderId);
                    cmd.ExecuteNonQuery();
                }

                AfterUnbind(connection, transaction, existingContactId.Value);
            }
            else
            {
                if (isPrimary)
                {
                    ClearPrimary(connection, transaction, contactId);
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        UPDATE contact_senders
                        SET account_label = @label,
                            is_primary = @isPrimary
                        WHERE contact_id = @cid AND sender_id = @sid;
                        """;
                    cmd.Parameters.AddWithValue("@cid", contactId);
                    cmd.Parameters.AddWithValue("@sid", senderId);
                    cmd.Parameters.AddWithValue("@label", (object?)accountLabel ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@isPrimary", isPrimary ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }

                Touch(connection, transaction, contactId);
                return;
            }
        }

        if (isPrimary)
        {
            ClearPrimary(connection, transaction, contactId);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO contact_senders(contact_id, sender_id, account_label, is_primary, created_at)
                VALUES (@cid, @sid, @label, @isPrimary, CURRENT_TIMESTAMP);
                """;
            cmd.Parameters.AddWithValue("@cid", contactId);
            cmd.Parameters.AddWithValue("@sid", senderId);
            cmd.Parameters.AddWithValue("@label", (object?)accountLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isPrimary", isPrimary ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        Touch(connection, transaction, contactId);
    }

    private static List<SenderConversationInfo> LoadConversationsForContact(
        SqliteConnection connection,
        long contactId,
        string contactDisplayName)
    {
        var result = new List<SenderConversationInfo>();
        var convSenderMap = new Dictionary<long, List<long>>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.id, c.title, MIN(m.timestamp_ms), MAX(m.timestamp_ms), COUNT(m.id)
                FROM messages m
                JOIN conversations c ON c.id = m.conversation_id
                JOIN contact_senders cs ON cs.sender_id = m.sender_id
                WHERE cs.contact_id = @id
                GROUP BY c.id
                ORDER BY MAX(m.timestamp_ms) DESC, c.id DESC;
                """;
            cmd.Parameters.AddWithValue("@id", contactId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SenderConversationInfo(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    contactDisplayName,
                    reader.GetInt64(4),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)
                ));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT m.conversation_id, m.sender_id
                FROM messages m
                JOIN contact_senders cs ON cs.sender_id = m.sender_id
                WHERE cs.contact_id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", contactId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var convId = reader.GetInt64(0);
                var senderId = reader.GetInt64(1);
                if (!convSenderMap.TryGetValue(convId, out var senders))
                {
                    senders = new List<long>();
                    convSenderMap[convId] = senders;
                }
                senders.Add(senderId);
            }
        }

        var keysToResolve = new List<(long SenderId, long? ConversationId)>();
        foreach (var (convId, senders) in convSenderMap)
        {
            foreach (var senderId in senders)
            {
                keysToResolve.Add((senderId, convId));
            }
        }

        var resolved = SenderDisplayName.Resolve(connection, keysToResolve);

        var finalResult = new List<SenderConversationInfo>(result.Count);
        foreach (var conv in result)
        {
            string nameInConv = contactDisplayName;
            if (convSenderMap.TryGetValue(conv.ConversationId, out var sList))
            {
                foreach (var sId in sList)
                {
                    if (resolved.TryGetValue((sId, conv.ConversationId), out var resolvedName))
                    {
                        nameInConv = resolvedName;
                        break;
                    }
                }
            }

            finalResult.Add(conv with { NameInConversation = nameInConv });
        }

        return finalResult;
    }

    private static Dictionary<long, List<AliasInfo>> LoadAliasesBatch(
        SqliteConnection connection,
        IReadOnlyCollection<long> senderIds)
    {
        var result = new Dictionary<long, List<AliasInfo>>();
        if (senderIds.Count == 0)
        {
            return result;
        }

        var ids = senderIds.Distinct().ToList();
        foreach (var chunk in ids.Chunk(500))
        {
            var placeholders = string.Join(",", chunk.Select((_, i) => $"@s{i}"));
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT sender_id, alias, MIN(first_seen_at), MAX(last_seen_at), COUNT(DISTINCT conversation_id)
                FROM sender_aliases
                WHERE sender_id IN ({placeholders})
                GROUP BY sender_id, alias
                ORDER BY MAX(last_seen_at) DESC, alias
                """;
            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@s{i}", chunk[i]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var senderId = reader.GetInt64(0);
                var alias = reader.GetString(1);
                var firstSeen = reader.IsDBNull(2) ? null : (long?)reader.GetInt64(2);
                var lastSeen = reader.IsDBNull(3) ? null : (long?)reader.GetInt64(3);

                if (!result.TryGetValue(senderId, out var list))
                {
                    list = new List<AliasInfo>();
                    result[senderId] = list;
                }

                list.Add(new AliasInfo(alias, null, firstSeen, lastSeen));
            }
        }

        return result;
    }

    private static long ParseTimestampMs(object? value)
    {
        if (value is null or DBNull) return 0;
        if (value is long l) return l;
        var str = value.ToString();
        if (string.IsNullOrWhiteSpace(str)) return 0;
        if (long.TryParse(str, out var ms)) return ms;
        if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }
        return 0;
    }
}
