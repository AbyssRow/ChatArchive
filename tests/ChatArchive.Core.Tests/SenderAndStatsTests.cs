using ChatArchive.Core.Repositories;
using Xunit;

namespace ChatArchive.Core.Tests;

public class SenderAndStatsTests : IDisposable
{
    private readonly TestArchive _archive = new();

    [Fact]
    public void SenderProfile_aggregates_aliases_and_conversations()
    {
        var sender = _archive.AddSender("alice001", "Alice");
        var convA = TestArchive.AddConversation(_archive.Open(), "a", "老张");
        var convB = TestArchive.AddConversation(_archive.Open(), "b", "工作群", kind: "group", platform: "wechat");

        _archive.AddSenderAlias(sender, "Alice", conversationId: convA, lastSeenAt: 1_700_000_000_000);
        _archive.AddSenderAlias(sender, "小爱", conversationId: convA, lastSeenAt: 1_700_000_200_000);
        _archive.AddSenderAlias(sender, "小爱", conversationId: convB, lastSeenAt: 1_700_000_300_000);

        _archive.AddMessage(convA, sender, 1_700_000_100_000, "你好", platform: "wechat", senderName: "Alice");
        _archive.AddMessage(convB, sender, 1_700_000_300_000, "在吗", platform: "wechat", senderName: "小爱");

        var profile = new SenderRepository(_archive.Db).GetSender(sender);
        Assert.NotNull(profile);
        Assert.Equal(2, profile!.Aliases.Count);
        Assert.Equal("小爱", profile.Aliases[0].Alias);
        Assert.Equal(2, profile.Conversations.Count);
        Assert.Equal("工作群", profile.Conversations[0].Title);
    }

    [Fact]
    public void SenderProfile_qq_number_from_payload()
    {
        var sender = _archive.AddSender("alice001", "Alice");
        var conv = TestArchive.AddConversation(_archive.Open(), "c", "会话");

        var messageId = _archive.AddMessage(conv, sender, 1_700_000_000_000, "hi");
        using (var connection = _archive.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET raw_payload_json = @p WHERE id = @id";
            command.Parameters.AddWithValue("@p", """{"sender":{"uin":"123456789"}}""");
            command.Parameters.AddWithValue("@id", messageId);
            command.ExecuteNonQuery();
        }

        var profile = new SenderRepository(_archive.Db).GetSender(sender);
        Assert.NotNull(profile);
        Assert.Equal("123456789", profile!.QQNumber);
        Assert.Null(new SenderRepository(_archive.Db).GetSender(9999));
    }

    [Fact]
    public void Stats_counts_match_data()
    {
        var qqConv = TestArchive.AddConversation(_archive.Open(), "q1", "QQ会话");
        var wxGroup = TestArchive.AddConversation(_archive.Open(), "w1", "微信群", kind: "group", platform: "wechat");

        var msg1 = _archive.AddMessage(qqConv, null, 1_700_000_000_000, "一");
        _archive.AddMessage(wxGroup, null, 1_700_000_200_000, "二", platform: "wechat");

        var sha = _archive.NextHash();
        var mediaId = _archive.AddMediaObject(sha, 2048, "image/jpeg", @"E:\ChatArchive\media\x\y.jpg");
        _archive.AddAttachment(msg1, 0, isAvailable: true, mediaObjectId: mediaObjectId(mediaId));
        _archive.AddAttachment(msg1, 1, isAvailable: false);

        var stats = new StatsRepository(_archive.Db).GetStats();
        Assert.Equal(2, stats.TotalMessages);
        Assert.Equal(1, stats.QQMessages);
        Assert.Equal(1, stats.WeChatMessages);
        Assert.Equal(2, stats.TotalConversations);
        Assert.Equal(1, stats.GroupConversations);
        Assert.Equal(2, stats.AttachmentCount);
        Assert.Equal(1, stats.AvailableAttachments);
        Assert.Equal(1, stats.MissingAttachments);
        Assert.Equal(1, stats.MediaFileCount);
        Assert.Equal(2048, stats.MediaTotalBytes);
        Assert.Equal(1_700_000_000_000, stats.FirstMessageAt);
        Assert.Equal(1_700_000_200_000, stats.LastMessageAt);
    }

    [Fact]
    public void Stats_handles_text_html_sql_platforms_without_overwriting_wechat()
    {
        var textConv = TestArchive.AddConversation(_archive.Open(), "t1", "文本会话", kind: "private", platform: "text");
        var htmlConv = TestArchive.AddConversation(_archive.Open(), "h1", "HTML会话", kind: "private", platform: "html");
        var sqlConv = TestArchive.AddConversation(_archive.Open(), "s1", "SQL会话", kind: "private", platform: "sql");

        _archive.AddMessage(textConv, null, 1_700_000_000_000, "文本消息", platform: "text");
        _archive.AddMessage(htmlConv, null, 1_700_000_100_000, "网页消息", platform: "html");
        _archive.AddMessage(sqlConv, null, 1_700_000_200_000, "数据库消息", platform: "sql");

        var stats = new StatsRepository(_archive.Db).GetStats();
        Assert.Equal(3, stats.TotalMessages);
        Assert.Equal(0, stats.QQMessages);
        Assert.Equal(0, stats.WeChatMessages);
        Assert.Equal(3, stats.TotalConversations);
        Assert.Equal(3, stats.PrivateConversations);
        Assert.Equal(0, stats.GroupConversations);
    }

    [Fact]
    public void BatchMethods_HandleMoreThan1000Items_WithoutSqliteVariableOverflow()
    {
        using var conn = _archive.Open();
        var keys = Enumerable.Range(1, 1200)
            .Select(i => (SenderId: (long)i, ConversationId: (long?)null))
            .ToList();

        // 1. SenderDisplayName.Resolve
        var resolved = SenderDisplayName.Resolve(conn, keys);
        Assert.NotNull(resolved);

        // 2. ConversationRepository.LoadContacts
        var senderIds = Enumerable.Range(1, 1200).Select(i => (long)i).ToList();
        var contacts = ConversationRepository.LoadContacts(conn, senderIds);
        Assert.NotNull(contacts);

        // 3. ConversationRepository.LoadAttachments
        var messageIds = Enumerable.Range(1, 1200).Select(i => (long)i).ToList();
        var attachments = ConversationRepository.LoadAttachments(conn, messageIds);
        Assert.NotNull(attachments);
    }

    private static long mediaObjectId(long id) => id;

    public void Dispose() => _archive.Dispose();
}
