using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class ContactRepositoryTests : IDisposable
{
    private readonly TestArchive _archive;
    private readonly ContactRepository _repository;

    public ContactRepositoryTests()
    {
        _archive = new TestArchive();
        _repository = new ContactRepository(_archive.Db);
    }

    public void Dispose()
    {
        _archive.Dispose();
    }

    [Fact]
    public void CreateContact_WithoutBindings_CreatesContactSuccessfully()
    {
        var contactId = _repository.CreateContact("Alice", "avatars/alice.png", "A close friend");
        Assert.True(contactId > 0);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal(contactId, detail.Id);
        Assert.Equal("Alice", detail.DisplayName);
        Assert.Equal("avatars/alice.png", detail.CustomAvatarPath);
        Assert.Equal("A close friend", detail.Note);
        Assert.Empty(detail.Senders);
        Assert.Empty(detail.Conversations);
        Assert.Equal(0, detail.TotalMessageCount);

        var list = _repository.ListContacts();
        Assert.Single(list);
        Assert.Equal("Alice", list[0].DisplayName);
        Assert.True(list[0].CreatedAtMs > 0);
        Assert.True(list[0].UpdatedAtMs > 0);
    }

    [Fact]
    public void CreateContact_WithInitialBindings_BindsMultipleSenders()
    {
        var sender1 = _archive.AddSender("10001", "Alice QQ", platform: "qq");
        var sender2 = _archive.AddSender("wxid_alice", "Alice WeChat", platform: "wechat");

        var bindings = new[]
        {
            (SenderId: sender1, Label: (string?)"QQ大号", IsPrimary: true),
            (SenderId: sender2, Label: (string?)"微信工作号", IsPrimary: false),
        };

        var contactId = _repository.CreateContact("Alice", initialBindings: bindings);
        Assert.True(contactId > 0);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Senders.Count);

        var qqSender = detail.Senders.FirstOrDefault(s => s.SenderId == sender1);
        Assert.NotNull(qqSender);
        Assert.Equal("qq", qqSender.Platform);
        Assert.Equal("10001", qqSender.NativeId);
        Assert.Equal("QQ大号", qqSender.AccountLabel);
        Assert.True(qqSender.IsPrimary);

        var wxSender = detail.Senders.FirstOrDefault(s => s.SenderId == sender2);
        Assert.NotNull(wxSender);
        Assert.Equal("wechat", wxSender.Platform);
        Assert.Equal("wxid_alice", wxSender.NativeId);
        Assert.Equal("微信工作号", wxSender.AccountLabel);
        Assert.False(wxSender.IsPrimary);
    }

    [Fact]
    public void CreateContact_WithEmptyDisplayName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _repository.CreateContact(""));
        Assert.Throws<ArgumentException>(() => _repository.CreateContact("   "));
    }

    [Fact]
    public void UpdateContact_UpdatesBasicInformation()
    {
        var contactId = _repository.CreateContact("Bob", "avatars/bob.png", "Old note");

        _repository.UpdateContact(contactId, "Robert", "avatars/robert.png", "New note");

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal("Robert", detail.DisplayName);
        Assert.Equal("avatars/robert.png", detail.CustomAvatarPath);
        Assert.Equal("New note", detail.Note);
    }

    [Fact]
    public void UpdateContact_WithEmptyDisplayName_ThrowsArgumentException()
    {
        var contactId = _repository.CreateContact("Bob");
        Assert.Throws<ArgumentException>(() => _repository.UpdateContact(contactId, "", null, null));
    }

    [Fact]
    public void BindSender_CrossPlatformBinding_Succeeds()
    {
        var contactId = _repository.CreateContact("Charlie");
        var qqSender = _archive.AddSender("20002", "Charlie QQ", platform: "qq");
        var wxSender = _archive.AddSender("wxid_charlie", "Charlie WX", platform: "wechat");

        _repository.BindSender(contactId, qqSender, accountLabel: "QQ", isPrimary: true);
        _repository.BindSender(contactId, wxSender, accountLabel: "微信", isPrimary: false);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Senders.Count);
        Assert.Contains(detail.Senders, s => s.Platform == "qq" && s.AccountLabel == "QQ" && s.IsPrimary);
        Assert.Contains(detail.Senders, s => s.Platform == "wechat" && s.AccountLabel == "微信" && !s.IsPrimary);
    }

    [Fact]
    public void BindSender_SamePlatformMultipleAccounts_SucceedsWithLabels()
    {
        var contactId = _repository.CreateContact("David");
        var qqMain = _archive.AddSender("30001", "David Main", platform: "qq");
        var qqWork = _archive.AddSender("30002", "David Work", platform: "qq");

        _repository.BindSender(contactId, qqMain, accountLabel: "大号", isPrimary: true);
        _repository.BindSender(contactId, qqWork, accountLabel: "工作号", isPrimary: false);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Senders.Count);
        Assert.All(detail.Senders, s => Assert.Equal("qq", s.Platform));

        var main = detail.Senders.First(s => s.SenderId == qqMain);
        Assert.Equal("大号", main.AccountLabel);
        Assert.True(main.IsPrimary);

        var work = detail.Senders.First(s => s.SenderId == qqWork);
        Assert.Equal("工作号", work.AccountLabel);
        Assert.False(work.IsPrimary);
    }

    [Fact]
    public void UnbindSender_RemovesBinding()
    {
        var sender1 = _archive.AddSender("40001", "Eva 1");
        var sender2 = _archive.AddSender("40002", "Eva 2");
        var contactId = _repository.CreateContact("Eva", initialBindings: new[]
        {
            (sender1, (string?)"1号", true),
            (sender2, (string?)"2号", false)
        });

        _repository.UnbindSender(contactId, sender1);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Single(detail.Senders);
        Assert.Equal(sender2, detail.Senders[0].SenderId);

        var unbound = _repository.ListUnboundSenders();
        Assert.Contains(unbound, s => s.SenderId == sender1);
        Assert.DoesNotContain(unbound, s => s.SenderId == sender2);
    }

    [Fact]
    public void UpdateAccountLabel_UpdatesLabelSuccessfully()
    {
        var sender = _archive.AddSender("50001", "Frank");
        var contactId = _repository.CreateContact("Frank", initialBindings: new[]
        {
            (sender, (string?)"临时号", true)
        });

        _repository.UpdateAccountLabel(contactId, sender, "常用号");

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal("常用号", detail.Senders[0].AccountLabel);
    }

    [Fact]
    public void SetPrimarySender_SwitchesPrimaryAccount()
    {
        var sender1 = _archive.AddSender("60001", "Grace 1");
        var sender2 = _archive.AddSender("60002", "Grace 2");
        var contactId = _repository.CreateContact("Grace", initialBindings: new[]
        {
            (sender1, (string?)"号1", true),
            (sender2, (string?)"号2", false)
        });

        _repository.SetPrimarySender(contactId, sender2);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);

        var s1 = detail.Senders.First(s => s.SenderId == sender1);
        var s2 = detail.Senders.First(s => s.SenderId == sender2);

        Assert.False(s1.IsPrimary);
        Assert.True(s2.IsPrimary);
    }

    [Fact]
    public void BindSender_ConflictWithoutForceRebind_ThrowsInvalidOperationException()
    {
        var sender = _archive.AddSender("70001", "Hank");
        var contact1 = _repository.CreateContact("Contact 1");
        var contact2 = _repository.CreateContact("Contact 2");

        _repository.BindSender(contact1, sender, accountLabel: "原绑定");

        // Attempt to bind already bound sender without force
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _repository.BindSender(contact2, sender, accountLabel: "新绑定", forceRebind: false));

        Assert.Contains("已绑定", ex.Message);

        // Verification: sender is still with contact 1
        var detail1 = _repository.GetContactDetail(contact1);
        Assert.NotNull(detail1);
        Assert.Single(detail1.Senders);

        var detail2 = _repository.GetContactDetail(contact2);
        Assert.NotNull(detail2);
        Assert.Empty(detail2.Senders);
    }

    [Fact]
    public void BindSender_ConflictWithForceRebind_TransfersSuccessfully()
    {
        var sender = _archive.AddSender("80001", "Ivy");
        var contact1 = _repository.CreateContact("Contact 1");
        var contact2 = _repository.CreateContact("Contact 2");

        _repository.BindSender(contact1, sender, accountLabel: "旧标签");

        // Force transfer to contact2
        _repository.BindSender(contact2, sender, accountLabel: "新标签", isPrimary: true, forceRebind: true);

        var detail1 = _repository.GetContactDetail(contact1);
        Assert.NotNull(detail1);
        Assert.Empty(detail1.Senders);

        var detail2 = _repository.GetContactDetail(contact2);
        Assert.NotNull(detail2);
        Assert.Single(detail2.Senders);
        Assert.Equal(sender, detail2.Senders[0].SenderId);
        Assert.Equal("新标签", detail2.Senders[0].AccountLabel);
        Assert.True(detail2.Senders[0].IsPrimary);

        var found = _repository.FindContactBySenderId(sender);
        Assert.NotNull(found);
        Assert.Equal(contact2, found.Id);
    }

    [Fact]
    public void DeleteContact_CascadeDeletesBindings_RetainsSendersAndMessages()
    {
        var convId = _archive.AddConversation("conv_jack", "Jack Chat");
        var senderId = _archive.AddSender("90001", "Jack");
        _archive.AddMessage(convId, senderId, 1000, "Hello");

        var contactId = _repository.CreateContact("Jack", initialBindings: new[]
        {
            (senderId, (string?)"默认", true)
        });

        _repository.DeleteContact(contactId);

        // Contact is gone
        Assert.Null(_repository.GetContactDetail(contactId));
        Assert.Null(_repository.FindContactBySenderId(senderId));

        // Sender and message still exist in archive
        using var conn = _archive.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM senders WHERE id = @s; SELECT COUNT(*) FROM messages WHERE sender_id = @s;";
        cmd.Parameters.AddWithValue("@s", senderId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));

        // Sender is now in unbound senders list
        var unbound = _repository.ListUnboundSenders();
        Assert.Contains(unbound, s => s.SenderId == senderId);
    }

    [Fact]
    public void GetContactDetail_CalculatesTotalMessagesAndLoadsConversations()
    {
        var conv1 = _archive.AddConversation("conv_1", "QQ Chat", platform: "qq");
        var conv2 = _archive.AddConversation("conv_2", "WX Chat", platform: "wechat");

        var sender1 = _archive.AddSender("qq_karen", "Karen QQ", platform: "qq");
        var sender2 = _archive.AddSender("wx_karen", "Karen WX", platform: "wechat");

        _archive.AddMessage(conv1, sender1, 1000, "Msg 1", platform: "qq");
        _archive.AddMessage(conv1, sender1, 2000, "Msg 2", platform: "qq");
        _archive.AddMessage(conv1, sender1, 3000, "Msg 3", platform: "qq");

        _archive.AddMessage(conv2, sender2, 4000, "Msg 4", platform: "wechat");
        _archive.AddMessage(conv2, sender2, 5000, "Msg 5", platform: "wechat");

        var contactId = _repository.CreateContact("Karen", initialBindings: new[]
        {
            (sender1, (string?)"QQ", true),
            (sender2, (string?)"微信", false)
        });

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal(5, detail.TotalMessageCount);

        var s1 = detail.Senders.First(s => s.SenderId == sender1);
        Assert.Equal(3, s1.MessageCount);

        var s2 = detail.Senders.First(s => s.SenderId == sender2);
        Assert.Equal(2, s2.MessageCount);

        Assert.Equal(2, detail.Conversations.Count);
        Assert.Contains(detail.Conversations, c => c.ConversationId == conv1 && c.MessageCount == 3);
        Assert.Contains(detail.Conversations, c => c.ConversationId == conv2 && c.MessageCount == 2);
    }

    [Fact]
    public void FindContactBySenderId_ReturnsContactInfoWhenBound_ReturnsNullWhenUnbound()
    {
        var sender1 = _archive.AddSender("leo_1", "Leo 1");
        var sender2 = _archive.AddSender("leo_2", "Leo 2");

        var conv = _archive.AddConversation("c_leo", "Leo Conversation");
        _archive.AddMessage(conv, sender1, 1000, "Hi");

        var contactId = _repository.CreateContact("Leo", note: "Test note", initialBindings: new[]
        {
            (sender1, (string?)"Main", true)
        });

        var contact = _repository.FindContactBySenderId(sender1);
        Assert.NotNull(contact);
        Assert.Equal(contactId, contact.Id);
        Assert.Equal("Leo", contact.DisplayName);
        Assert.Equal("Test note", contact.Note);
        Assert.Equal(1, contact.MessageCount);

        var nullContact = _repository.FindContactBySenderId(sender2);
        Assert.Null(nullContact);
    }

    [Fact]
    public void ListContacts_WithKeywordSearchAndStats()
    {
        var s1 = _archive.AddSender("alice_native", "Alice Native");
        var s2 = _archive.AddSender("bob_native", "Bob Native");
        _archive.AddSenderAlias(s1, "Alilove");

        var conv = _archive.AddConversation("c_list", "Chat");
        _archive.AddMessage(conv, s1, 1000, "M1");
        _archive.AddMessage(conv, s1, 2000, "M2");

        var c1 = _repository.CreateContact("Alice Smith", note: "Developer Colleague", initialBindings: new[]
        {
            (s1, (string?)"WorkAccount", true)
        });
        var c2 = _repository.CreateContact("Bob Jones", note: "High school friend", initialBindings: new[]
        {
            (s2, (string?)"Personal", true)
        });

        var all = _repository.ListContacts();
        Assert.Equal(2, all.Count);
        var aliceInfo = all.First(c => c.Id == c1);
        Assert.Equal(2, aliceInfo.MessageCount);

        // Search by Contact DisplayName
        var byName = _repository.ListContacts("Smith");
        Assert.Single(byName);
        Assert.Equal(c1, byName[0].Id);

        // Search by Note
        var byNote = _repository.ListContacts("Colleague");
        Assert.Single(byNote);
        Assert.Equal(c1, byNote[0].Id);

        // Search by Account Label
        var byLabel = _repository.ListContacts("WorkAccount");
        Assert.Single(byLabel);
        Assert.Equal(c1, byLabel[0].Id);

        // Search by Sender Native ID
        var byNative = _repository.ListContacts("alice_native");
        Assert.Single(byNative);
        Assert.Equal(c1, byNative[0].Id);

        // Search by Sender Alias
        var byAlias = _repository.ListContacts("Alilove");
        Assert.Single(byAlias);
        Assert.Equal(c1, byAlias[0].Id);

        // Search non-matching
        var empty = _repository.ListContacts("NonexistentString");
        Assert.Empty(empty);
    }

    [Fact]
    public void ListUnboundSenders_WithKeywordSearch()
    {
        var s1 = _archive.AddSender("wx_1", "Zack 1", platform: "wechat");
        var s2 = _archive.AddSender("qq_2", "Zack 2", platform: "qq");
        var s3 = _archive.AddSender("wx_3", "Wendy", platform: "wechat");

        var conv = _archive.AddConversation("c_unbound", "Chat");
        _archive.AddMessage(conv, s2, 1000, "M1");
        _archive.AddMessage(conv, s2, 2000, "M2");

        // Bind s1 to a contact
        _repository.CreateContact("Zack Contact", initialBindings: new[] { (s1, (string?)null, true) });

        // Unbound should only contain s2 and s3
        var unbound = _repository.ListUnboundSenders();
        Assert.Equal(2, unbound.Count);
        Assert.Equal(s2, unbound[0].SenderId); // Order by message count DESC
        Assert.Equal(2, unbound[0].MessageCount);
        Assert.Equal(s3, unbound[1].SenderId);
        Assert.Equal(0, unbound[1].MessageCount);

        // Keyword filter
        var filtered = _repository.ListUnboundSenders("Wendy");
        Assert.Single(filtered);
        Assert.Equal(s3, filtered[0].SenderId);

        var filteredNative = _repository.ListUnboundSenders("qq_2");
        Assert.Single(filteredNative);
        Assert.Equal(s2, filteredNative[0].SenderId);
    }

    [Fact]
    public void AutoPopulateContactsFromSenders_OnlyIncludesPrivateChatSenders_ExcludesGroupSenders()
    {
        // 1. Private chat sender (should be auto-populated)
        var privateSender = _archive.AddSender("wx_friend", "Private Friend", platform: "wechat");
        var privateConv = _archive.AddConversation("c_private", "Private Chat with Friend", kind: "private");
        _archive.AddMessage(privateConv, privateSender, 1000, "Hello");

        // 2. Group only sender (should NOT be auto-populated)
        var groupSender = _archive.AddSender("qq_stranger", "Group Stranger", platform: "qq");
        var groupConv = _archive.AddConversation("c_group", "Big Group Chat", kind: "group");
        _archive.AddMessage(groupConv, groupSender, 1000, "Group message");

        // 3. Self sender (should NOT be auto-populated)
        var selfSender = _archive.AddSender("wx_me", "Me", platform: "wechat", isSelf: true);
        _archive.AddMessage(privateConv, selfSender, 1001, "Hi there");

        // Run auto-populate
        var count = _repository.AutoPopulateContactsFromSenders();
        Assert.Equal(1, count);

        // Verify list of contacts
        var contacts = _repository.ListContacts();
        Assert.Single(contacts);
        Assert.Equal("Private Chat with Friend", contacts[0].DisplayName);

        // Verify detail
        var detail = _repository.GetContactDetail(contacts[0].Id);
        Assert.NotNull(detail);
        Assert.Single(detail.Senders);
        Assert.Equal(privateSender, detail.Senders[0].SenderId);

        // Running again should be idempotent (0 added)
        var secondRun = _repository.AutoPopulateContactsFromSenders();
        Assert.Equal(0, secondRun);
    }
}

