using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using Xunit;

namespace ChatArchive.Core.Tests;

public class ConversationRepositoryTests : IDisposable
{
    private readonly TestArchive _archive = new();
    private readonly ConversationRepository _repository;

    public ConversationRepositoryTests()
    {
        _repository = new ConversationRepository(_archive.Db);
    }

    [Fact]
    public void ListConversations_orders_and_filters()
    {
        var c1 = TestArchive.AddConversation(_archive.Open(), "c1", "老张", lastMessageAt: 1000);
        var c2 = TestArchive.AddConversation(_archive.Open(), "c2", "工作群", kind: "group", platform: "wechat", lastMessageAt: 2000);
        var c3 = TestArchive.AddConversation(_archive.Open(), "c3", "小李", lastMessageAt: 3000);

        TestArchive.AddMessage(_archive.Open(), c1, null, 1000, "早");
        TestArchive.AddMessage(_archive.Open(), c2, null, 2000, "收到");

        var all = _repository.ListConversations();
        Assert.Equal(3, all.Count);
        Assert.Equal(c3, all[0].Id);
        Assert.Equal("收到", all[1].LastMessagePreview);

        Assert.Single(_repository.ListConversations(platform: "wechat"));
        Assert.Single(_repository.ListConversations(kind: "group"));
        Assert.Single(_repository.ListConversations(query: "老张"));

        _archive.AddAlias(c1, "张三");
        Assert.Single(_repository.ListConversations(query: "张三"));
    }

    [Fact]
    public void GetConversation_returns_aliases()
    {
        var id = TestArchive.AddConversation(_archive.Open(), "c1", "老张");
        _archive.AddAlias(id, "张三");

        var detail = _repository.GetConversation(id);
        Assert.NotNull(detail);
        Assert.Single(detail!.Aliases);
        Assert.Equal("张三", detail.Aliases[0]);
        Assert.Null(_repository.GetConversation(9999));
    }

    [Fact]
    public void ListMessages_paginates_chronologically()
    {
        var conv = TestArchive.AddConversation(_archive.Open(), "c1", "会话");
        for (var i = 0; i < 5; i++)
        {
            _archive.AddMessage(conv, null, 1_700_000_000_000 + i * 1000, $"消息{i}");
        }

        var page1 = _repository.ListMessages(conv, limit: 2);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal("消息3", page1.Items[0].Content);
        Assert.Equal("消息4", page1.Items[1].Content);
        Assert.NotNull(page1.NextCursor);

        var page2 = _repository.ListMessages(conv, cursor: page1.NextCursor, limit: 2);
        Assert.Equal("消息1", page2.Items[0].Content);
        Assert.Equal("消息2", page2.Items[1].Content);
        Assert.NotNull(page2.NextCursor);

        var page3 = _repository.ListMessages(conv, cursor: page2.NextCursor, limit: 2);
        Assert.Single(page3.Items);
        Assert.Equal("消息0", page3.Items[0].Content);
        Assert.Null(page3.NextCursor);
    }

    [Fact]
    public void GetMessageContext_returns_window()
    {
        var conv = TestArchive.AddConversation(_archive.Open(), "c1", "会话");
        long middle = 0;
        for (var i = 0; i < 5; i++)
        {
            var id = _archive.AddMessage(conv, null, 1_700_000_000_000 + i * 1000, $"消息{i}");
            if (i == 2)
            {
                middle = id;
            }
        }

        var context = _repository.GetMessageContext(middle, radius: 1);
        Assert.NotNull(context);
        Assert.Equal(3, context!.Messages.Count);
        Assert.Equal("消息1", context.Messages[0].Content);
        Assert.Equal("消息2", context.Messages[1].Content);
        Assert.Equal("消息3", context.Messages[2].Content);
        Assert.Equal(middle, context!.FocusMessageId);
        Assert.Null(_repository.GetMessageContext(9999));
    }

    [Fact]
    public void Hydrate_loads_attachments_and_missing_media_counts()
    {
        var conv = TestArchive.AddConversation(_archive.Open(), "c1", "会话");
        var msg = _archive.AddMessage(conv, null, 1_700_000_000_000, "[图片]");

        var sha = _archive.NextHash();
        var mediaId = _archive.AddMediaObject(sha, 123, "image/jpeg", $@"E:\ChatArchive\media\{sha[..2]}\{sha}.jpg");
        _archive.AddAttachment(msg, 0, filename: "a.jpg", isAvailable: true, mediaObjectId: mediaObjectId(mediaId));
        _archive.AddAttachment(msg, 1, filename: "b.jpg", isAvailable: false);

        var page = _repository.ListMessages(conv);
        var attachments = page.Items[0].Attachments;
        Assert.Equal(2, attachments.Count);
        Assert.True(attachments[0].IsAvailable);
        Assert.Equal(sha, attachments[0].MediaSha256);
        Assert.False(attachments[1].IsAvailable);

        var list = new ConversationRepository(_archive.Db).ListConversations();
        Assert.Equal(1, list[0].MissingMediaCount);
    }

    [Fact]
    public void Hydrate_loads_contact_display_name_avatar_and_account_label()
    {
        var contactRepo = new ContactRepository(_archive.Db);
        var s1 = _archive.AddSender("s1_native", "小张", platform: "wechat");
        var s2 = _archive.AddSender("s2_native", "李四", platform: "wechat");

        var contactId = contactRepo.CreateContact("张总", "avatars/zhang.png", initialBindings: new[]
        {
            (s1, (string?)"工作号", true)
        });

        var conv = TestArchive.AddConversation(_archive.Open(), "c1", "会话");
        var m1 = _archive.AddMessage(conv, s1, 1_700_000_000_000, "你好啊", senderName: "小张快照");
        var m2 = _archive.AddMessage(conv, s2, 1_700_000_001_000, "收到", senderName: "李四快照");

        var page = _repository.ListMessages(conv);
        Assert.Equal(2, page.Items.Count);

        var item1 = page.Items.First(i => i.Id == m1);
        Assert.Equal("张总", item1.SenderName);
        Assert.Equal("avatars/zhang.png", item1.CustomAvatarPath);
        Assert.Equal("工作号", item1.AccountLabel);

        var item2 = page.Items.First(i => i.Id == m2);
        Assert.Equal("李四快照", item2.SenderName);
        Assert.Null(item2.CustomAvatarPath);
        Assert.Null(item2.AccountLabel);

        var context = _repository.GetMessageContext(m1, radius: 2);
        Assert.NotNull(context);
        var ctxItem1 = context!.Messages.First(i => i.Id == m1);
        Assert.Equal("张总", ctxItem1.SenderName);
        Assert.Equal("avatars/zhang.png", ctxItem1.CustomAvatarPath);
        Assert.Equal("工作号", ctxItem1.AccountLabel);
    }

    [Theory]
    [InlineData("invalid_cursor")]
    [InlineData("-1_abc")]
    [InlineData("abc")]
    [InlineData("_123")]
    public void ListMessages_WithInvalidCursor_FallsBackGracefully(string invalidCursor)
    {
        var conv = TestArchive.AddConversation(_archive.Open(), "c1", "会话");
        _archive.AddMessage(conv, null, 1_700_000_000_000, "测试消息");

        var page = _repository.ListMessages(conv, cursor: invalidCursor);
        Assert.Single(page.Items);
        Assert.Equal("测试消息", page.Items[0].Content);
    }

    private static long mediaObjectId(long id) => id;

    public void Dispose() => _archive.Dispose();
}
