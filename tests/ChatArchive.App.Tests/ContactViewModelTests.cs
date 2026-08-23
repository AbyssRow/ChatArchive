using ChatArchive.App.ViewModels;
using ChatArchive.Core.Data;
using ChatArchive.Core.Repositories;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class ContactViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-contact-vm-{Guid.NewGuid():N}");

    [Fact]
    public async Task Qq_identity_prefers_number_from_message_payload()
    {
        Directory.CreateDirectory(_directory);
        var database = new ArchiveDatabase(Path.Combine(_directory, "contact.db"));
        database.EnsureSchema();

        long senderId;
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversations(platform, account_id, native_id, kind, title)
                VALUES ('qq', 'account', 'conversation', 'private', '会话');
                INSERT INTO senders(platform, account_id, native_id, current_name, is_self)
                VALUES ('qq', 'account', 'uid-internal', '联系人', 0);
                INSERT INTO messages(
                    conversation_id, sender_id, platform, timestamp_ms, direction, message_type,
                    content, search_text, sender_name_snapshot, conversation_title_snapshot,
                    is_recalled, is_system, payload_hash, semantic_hash, raw_payload_json)
                VALUES (
                    1, 1, 'qq', 1700000000000, 'incoming', 'text',
                    '你好', '你好', '联系人', '会话', 0, 0, 'payload', 'semantic',
                    '{"sender":{"uin":"123456789"}}');
                SELECT id FROM senders LIMIT 1;
                """;
            senderId = Convert.ToInt64(command.ExecuteScalar());
        }

        var viewModel = new ContactViewModel(new SenderRepository(database));

        Assert.True(await viewModel.LoadAsync(senderId));
        Assert.Equal("QQ 123456789", viewModel.IdentityLine);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup for test artifacts.
        }
    }
}
