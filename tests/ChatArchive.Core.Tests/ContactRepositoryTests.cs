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
        Assert.Equal(32, detail.IdentityToken.Length);
        Assert.Empty(detail.Senders);
        Assert.Empty(detail.Conversations);
        Assert.Equal(0, detail.TotalMessageCount);

        var list = _repository.ListContacts();
        Assert.Single(list);
        Assert.Equal("Alice", list[0].DisplayName);
        Assert.Equal(detail.IdentityToken, list[0].IdentityToken);
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
    public void UnbindSender_WhenPrimarySenderUnbound_PromotesFirstRemainingSenderToPrimary()
    {
        var sender1 = _archive.AddSender("41001", "Eva 1");
        var sender2 = _archive.AddSender("41002", "Eva 2");
        var sender3 = _archive.AddSender("41003", "Eva 3");
        var contactId = _repository.CreateContact("Eva Multi", initialBindings: new[]
        {
            (sender1, (string?)"1号", true),
            (sender2, (string?)"2号", false),
            (sender3, (string?)"3号", false)
        });

        _repository.UnbindSender(contactId, sender1);

        var detail = _repository.GetContactDetail(contactId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Senders.Count);
        var remainingPrimary = detail.Senders.FirstOrDefault(s => s.IsPrimary);
        Assert.NotNull(remainingPrimary);
        Assert.Equal(sender2, remainingPrimary.SenderId);
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
        Assert.Null(detail1); // Empty contact1 without notes/avatars is automatically cleaned up

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
    public void BindSenderToExpectedContact_WhenTargetIdIsReused_RejectsStaleTargetIdentity()
    {
        var sender = _archive.AddSender("80501", "Unbound target ABA sender");
        var deletedTarget = _repository.CreateContact("Deleted unbound target");
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(deletedTarget));
        var senderSnapshot = Assert.Single(
            _repository.ListAvailableSendersToBind(deletedTarget),
            item => item.SenderId == sender);
        Assert.Null(senderSnapshot.BoundContactId);

        _repository.DeleteContact(deletedTarget);
        var replacementTarget = _repository.CreateContact("Replacement unbound target");
        var replacementSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(replacementTarget));
        Assert.Equal(deletedTarget, replacementTarget);
        Assert.NotEqual(targetSnapshot.IdentityToken, replacementSnapshot.IdentityToken);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.BindSenderToExpectedContact(
                replacementTarget,
                targetSnapshot.IdentityToken,
                sender,
                accountLabel: "不应写入",
                isPrimary: true));

        Assert.Contains("目标联系人", exception.Message);
        Assert.Null(_repository.FindContactBySenderId(sender));
        Assert.Empty(_repository.GetContactDetail(replacementTarget)!.Senders);
    }

    [Fact]
    public void BindSenderToExpectedContact_WhenTargetTokenMismatches_RejectsWithoutSideEffects()
    {
        var sender = _archive.AddSender("80502", "Target token mismatch sender");
        var existingPrimary = _archive.AddSender("80509", "Token mismatch existing primary");
        var targetContact = _repository.CreateContact(
            "Token mismatch target",
            initialBindings: [(existingPrimary, "保留主账号", true)]);
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));
        var mismatchedToken = (targetSnapshot.IdentityToken[0] == '0' ? "1" : "0")
            + targetSnapshot.IdentityToken[1..];

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.BindSenderToExpectedContact(
                targetContact,
                mismatchedToken,
                sender,
                accountLabel: "不应写入",
                isPrimary: true));

        Assert.Contains("目标联系人", exception.Message);
        Assert.Null(_repository.FindContactBySenderId(sender));
        var unchangedPrimary = Assert.Single(_repository.GetContactDetail(targetContact)!.Senders);
        Assert.Equal(existingPrimary, unchangedPrimary.SenderId);
        Assert.Equal("保留主账号", unchangedPrimary.AccountLabel);
        Assert.True(unchangedPrimary.IsPrimary);
    }

    [Fact]
    public void BindSenderToExpectedContact_WhenSenderWasBoundElsewhereAfterSnapshot_RejectsWithoutSideEffects()
    {
        var sender = _archive.AddSender("80503", "Bound elsewhere after snapshot");
        var targetContact = _repository.CreateContact("Expected unbound target");
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));
        Assert.Contains(
            _repository.ListAvailableSendersToBind(targetContact),
            item => item.SenderId == sender && item.BoundContactId is null);
        var currentOwner = _repository.CreateContact("Current owner", note: "必须保留");
        _repository.BindSender(currentOwner, sender, accountLabel: "现任标签", isPrimary: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.BindSenderToExpectedContact(
                targetContact,
                targetSnapshot.IdentityToken,
                sender,
                accountLabel: "不应覆盖",
                isPrimary: false));

        Assert.Contains("重试", exception.Message);
        Assert.Empty(_repository.GetContactDetail(targetContact)!.Senders);
        var unchangedBinding = Assert.Single(_repository.GetContactDetail(currentOwner)!.Senders);
        Assert.Equal("现任标签", unchangedBinding.AccountLabel);
        Assert.True(unchangedBinding.IsPrimary);
        Assert.Equal(currentOwner, _repository.FindContactBySenderId(sender)!.Id);
    }

    [Fact]
    public void BindSenderToExpectedContact_WhenSenderWasBoundToTargetAfterSnapshot_RejectsWithoutUpdatingBinding()
    {
        var existingPrimary = _archive.AddSender("80504", "Existing target primary");
        var sender = _archive.AddSender("80505", "Bound to target after snapshot");
        var targetContact = _repository.CreateContact(
            "Same target owner",
            initialBindings: [(existingPrimary, "原主账号", true)]);
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));
        Assert.Contains(
            _repository.ListAvailableSendersToBind(targetContact),
            item => item.SenderId == sender && item.BoundContactId is null);
        _repository.BindSender(targetContact, sender, accountLabel: "现有标签", isPrimary: false);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.BindSenderToExpectedContact(
                targetContact,
                targetSnapshot.IdentityToken,
                sender,
                accountLabel: "不应覆盖",
                isPrimary: true));

        Assert.Contains("重试", exception.Message);
        var unchangedTarget = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));
        var unchangedPrimary = unchangedTarget.Senders.Single(item => item.SenderId == existingPrimary);
        var unchangedSender = unchangedTarget.Senders.Single(item => item.SenderId == sender);
        Assert.Equal("原主账号", unchangedPrimary.AccountLabel);
        Assert.True(unchangedPrimary.IsPrimary);
        Assert.Equal("现有标签", unchangedSender.AccountLabel);
        Assert.False(unchangedSender.IsPrimary);
    }

    [Fact]
    public void BindSenderToExpectedContact_WhenSnapshotStillMatches_BindsSuccessfully()
    {
        var sender = _archive.AddSender("80506", "Expected unbound sender");
        var targetContact = _repository.CreateContact("Expected target");
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));

        _repository.BindSenderToExpectedContact(
            targetContact,
            targetSnapshot.IdentityToken,
            sender,
            accountLabel: "安全绑定",
            isPrimary: true);

        var binding = Assert.Single(_repository.GetContactDetail(targetContact)!.Senders);
        Assert.Equal(sender, binding.SenderId);
        Assert.Equal("安全绑定", binding.AccountLabel);
        Assert.True(binding.IsPrimary);
        Assert.Equal(targetContact, _repository.FindContactBySenderId(sender)!.Id);
    }

    [Fact]
    public void BindSenderToExpectedContact_WhenInsertFails_RollsBackTargetPrimaryChange()
    {
        var existingPrimary = _archive.AddSender("80507", "Rollback existing primary");
        var sender = _archive.AddSender("80508", "Rollback unbound sender");
        var targetContact = _repository.CreateContact(
            "Rollback expected target",
            initialBindings: [(existingPrimary, "保留主账号", true)]);
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));

        using (var connection = _archive.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER fail_expected_unbound_target_insert
                BEFORE INSERT ON contact_senders
                WHEN NEW.contact_id = {targetContact} AND NEW.sender_id = {sender}
                BEGIN
                    SELECT RAISE(ABORT, 'forced expected target insert failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<SqliteException>(() =>
            _repository.BindSenderToExpectedContact(
                targetContact,
                targetSnapshot.IdentityToken,
                sender,
                accountLabel: "不应提交",
                isPrimary: true));

        Assert.Contains("forced expected target insert failure", exception.Message);
        Assert.Null(_repository.FindContactBySenderId(sender));
        var unchangedPrimary = Assert.Single(_repository.GetContactDetail(targetContact)!.Senders);
        Assert.Equal(existingPrimary, unchangedPrimary.SenderId);
        Assert.Equal("保留主账号", unchangedPrimary.AccountLabel);
        Assert.True(unchangedPrimary.IsPrimary);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenOwnerMatches_TransfersSuccessfully()
    {
        var sender = _archive.AddSender("81001", "Expected owner sender");
        var sourceContact = _repository.CreateContact(
            "Confirmed source",
            initialBindings: [(sender, "旧标签", true)]);
        var targetContact = _repository.CreateContact("Transfer target");

        _repository.TransferSenderFromExpectedContact(
            targetContact,
            sender,
            sourceContact,
            accountLabel: "新标签",
            isPrimary: true);

        Assert.Null(_repository.GetContactDetail(sourceContact));
        var targetDetail = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));
        var transferred = Assert.Single(targetDetail.Senders);
        Assert.Equal(sender, transferred.SenderId);
        Assert.Equal("新标签", transferred.AccountLabel);
        Assert.True(transferred.IsPrimary);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenTargetIdIsReused_RejectsStaleTargetIdentity()
    {
        var sender = _archive.AddSender("81101", "Target ABA sender");
        var sourceContact = _repository.CreateContact(
            "Target ABA source",
            note: "保留来源",
            initialBindings: [(sender, "原标签", true)]);
        var sourceSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(sourceContact));
        var deletedTarget = _repository.CreateContact("Deleted target");
        var deletedTargetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(deletedTarget));

        _repository.DeleteContact(deletedTarget);
        var replacementTarget = _repository.CreateContact("Replacement target");
        var replacementSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(replacementTarget));
        Assert.Equal(deletedTarget, replacementTarget);
        Assert.NotEqual(deletedTargetSnapshot.IdentityToken, replacementSnapshot.IdentityToken);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.TransferSenderFromExpectedContact(
                replacementTarget,
                deletedTargetSnapshot.IdentityToken,
                sender,
                sourceContact,
                sourceSnapshot.IdentityToken,
                accountLabel: "不应写入",
                isPrimary: true));

        Assert.Contains("目标联系人", exception.Message);
        Assert.Equal(sourceContact, _repository.FindContactBySenderId(sender)!.Id);
        Assert.True(Assert.Single(_repository.GetContactDetail(sourceContact)!.Senders).IsPrimary);
        Assert.Empty(_repository.GetContactDetail(replacementTarget)!.Senders);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenSourceIdIsReused_RejectsStaleSourceIdentity()
    {
        var sender = _archive.AddSender("81201", "Source ABA sender");
        var targetContact = _repository.CreateContact("Stable target");
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));
        var deletedSource = _repository.CreateContact(
            "Deleted source",
            initialBindings: [(sender, "旧来源", true)]);
        var deletedSourceSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(deletedSource));

        _repository.DeleteContact(deletedSource);
        var replacementSource = _repository.CreateContact("Replacement source");
        Assert.Equal(deletedSource, replacementSource);
        _repository.BindSender(replacementSource, sender, accountLabel: "新来源", isPrimary: true);
        var replacementSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(replacementSource));
        Assert.NotEqual(deletedSourceSnapshot.IdentityToken, replacementSnapshot.IdentityToken);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.TransferSenderFromExpectedContact(
                targetContact,
                targetSnapshot.IdentityToken,
                sender,
                deletedSource,
                deletedSourceSnapshot.IdentityToken,
                accountLabel: "不应写入",
                isPrimary: true));

        Assert.Contains("来源联系人", exception.Message);
        Assert.Equal(replacementSource, _repository.FindContactBySenderId(sender)!.Id);
        Assert.Equal("新来源", Assert.Single(_repository.GetContactDetail(replacementSource)!.Senders).AccountLabel);
        Assert.Empty(_repository.GetContactDetail(targetContact)!.Senders);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenEitherIdentityTokenMismatches_RejectsWithoutSideEffects()
    {
        var sender = _archive.AddSender("81301", "Token mismatch sender");
        var sourceContact = _repository.CreateContact(
            "Token source",
            note: "保留来源",
            initialBindings: [(sender, "原标签", true)]);
        var targetContact = _repository.CreateContact("Token target");
        var sourceSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(sourceContact));
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));

        Assert.Throws<InvalidOperationException>(() =>
            _repository.TransferSenderFromExpectedContact(
                targetContact,
                new string('0', 32),
                sender,
                sourceContact,
                sourceSnapshot.IdentityToken));
        Assert.Throws<InvalidOperationException>(() =>
            _repository.TransferSenderFromExpectedContact(
                targetContact,
                targetSnapshot.IdentityToken,
                sender,
                sourceContact,
                new string('f', 32)));

        Assert.Equal(sourceContact, _repository.FindContactBySenderId(sender)!.Id);
        Assert.Equal("原标签", Assert.Single(_repository.GetContactDetail(sourceContact)!.Senders).AccountLabel);
        Assert.Empty(_repository.GetContactDetail(targetContact)!.Senders);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenTransferredSenderWasPrimary_PromotesRemainingSourceSender()
    {
        var transferredSender = _archive.AddSender("81401", "Transferred primary");
        var remainingSender = _archive.AddSender("81402", "Remaining sender");
        var sourceContact = _repository.CreateContact(
            "Promotion source",
            initialBindings:
            [
                (transferredSender, "主账号", true),
                (remainingSender, "保留账号", false),
            ]);
        var targetContact = _repository.CreateContact("Promotion target");
        var sourceSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(sourceContact));
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));

        _repository.TransferSenderFromExpectedContact(
            targetContact,
            targetSnapshot.IdentityToken,
            transferredSender,
            sourceContact,
            sourceSnapshot.IdentityToken,
            accountLabel: "已转移",
            isPrimary: true);

        var sourceDetail = Assert.IsType<ContactDetail>(_repository.GetContactDetail(sourceContact));
        var promoted = Assert.Single(sourceDetail.Senders);
        Assert.Equal(remainingSender, promoted.SenderId);
        Assert.True(promoted.IsPrimary);
        Assert.True(Assert.Single(_repository.GetContactDetail(targetContact)!.Senders).IsPrimary);
    }

    [Fact]
    public void BindSender_ForceRebind_WhenTransferredSenderWasPrimary_PromotesRemainingSourceSender()
    {
        var transferredSender = _archive.AddSender("81501", "Legacy transferred primary");
        var remainingSender = _archive.AddSender("81502", "Legacy remaining sender");
        var sourceContact = _repository.CreateContact(
            "Legacy promotion source",
            initialBindings:
            [
                (transferredSender, "主账号", true),
                (remainingSender, "保留账号", false),
            ]);
        var targetContact = _repository.CreateContact("Legacy promotion target");

        _repository.BindSender(
            targetContact,
            transferredSender,
            accountLabel: "已转移",
            isPrimary: true,
            forceRebind: true);

        var promoted = Assert.Single(_repository.GetContactDetail(sourceContact)!.Senders);
        Assert.Equal(remainingSender, promoted.SenderId);
        Assert.True(promoted.IsPrimary);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenOwnerChanged_RejectsWithoutSideEffects()
    {
        var sender = _archive.AddSender("82001", "Moved sender");
        var existingTargetSender = _archive.AddSender("82002", "Existing target sender");
        var confirmedSource = _repository.CreateContact(
            "Confirmed source",
            note: "必须保留",
            initialBindings: [(sender, "来源标签", true)]);
        var currentOwner = _repository.CreateContact(
            "Current owner",
            initialBindings: [(existingTargetSender, "现任主账号", true)]);
        var requestedTarget = _repository.CreateContact("Requested target");
        var snapshot = Assert.Single(
            _repository.ListAvailableSendersToBind(requestedTarget),
            item => item.SenderId == sender);
        Assert.Equal(confirmedSource, snapshot.BoundContactId);

        _repository.BindSender(
            currentOwner,
            sender,
            accountLabel: "现任标签",
            isPrimary: false,
            forceRebind: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.TransferSenderFromExpectedContact(
                requestedTarget,
                sender,
                snapshot.BoundContactId!.Value,
                accountLabel: "错误新标签",
                isPrimary: true));

        Assert.Contains("重试", exception.Message);
        var sourceDetail = Assert.IsType<ContactDetail>(_repository.GetContactDetail(confirmedSource));
        Assert.Equal("必须保留", sourceDetail.Note);
        Assert.Empty(sourceDetail.Senders);

        var currentDetail = Assert.IsType<ContactDetail>(_repository.GetContactDetail(currentOwner));
        Assert.Equal(2, currentDetail.Senders.Count);
        var unchangedSender = currentDetail.Senders.Single(item => item.SenderId == sender);
        Assert.Equal("现任标签", unchangedSender.AccountLabel);
        Assert.False(unchangedSender.IsPrimary);
        var unchangedPrimary = currentDetail.Senders.Single(item => item.SenderId == existingTargetSender);
        Assert.Equal("现任主账号", unchangedPrimary.AccountLabel);
        Assert.True(unchangedPrimary.IsPrimary);

        Assert.Empty(Assert.IsType<ContactDetail>(_repository.GetContactDetail(requestedTarget)).Senders);
        Assert.Equal(currentOwner, _repository.FindContactBySenderId(sender)!.Id);
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenSenderBecameUnbound_RejectsWithoutBindingTarget()
    {
        var sender = _archive.AddSender("83001", "Unbound sender");
        var confirmedSource = _repository.CreateContact(
            "Confirmed source",
            note: "保留联系人",
            initialBindings: [(sender, "快照标签", true)]);
        var targetContact = _repository.CreateContact("Transfer target");
        var snapshot = Assert.Single(
            _repository.ListAvailableSendersToBind(targetContact),
            item => item.SenderId == sender);
        Assert.Equal(confirmedSource, snapshot.BoundContactId);

        _repository.UnbindSender(confirmedSource, sender);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _repository.TransferSenderFromExpectedContact(
                targetContact,
                sender,
                snapshot.BoundContactId!.Value,
                accountLabel: "不应写入",
                isPrimary: true));

        Assert.Contains("重试", exception.Message);
        Assert.Empty(Assert.IsType<ContactDetail>(_repository.GetContactDetail(confirmedSource)).Senders);
        Assert.Empty(Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact)).Senders);
        Assert.Null(_repository.FindContactBySenderId(sender));
    }

    [Fact]
    public void TransferSenderFromExpectedContact_WhenTargetInsertFails_RollsBackSourceDeleteAndCleanup()
    {
        var sender = _archive.AddSender("84001", "Rollback sender");
        var remainingSender = _archive.AddSender("84002", "Rollback remaining sender");
        var sourceContact = _repository.CreateContact(
            "Rollback source",
            initialBindings:
            [
                (sender, "原标签", true),
                (remainingSender, "保留标签", false),
            ]);
        var targetContact = _repository.CreateContact("Rollback target");
        var sourceSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(sourceContact));
        var targetSnapshot = Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact));

        using (var connection = _archive.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER fail_expected_owner_target_insert
                BEFORE INSERT ON contact_senders
                WHEN NEW.contact_id = {targetContact}
                BEGIN
                    SELECT RAISE(ABORT, 'forced target insert failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<SqliteException>(() =>
            _repository.TransferSenderFromExpectedContact(
                targetContact,
                targetSnapshot.IdentityToken,
                sender,
                sourceContact,
                sourceSnapshot.IdentityToken,
                accountLabel: "不应提交",
                isPrimary: false));

        Assert.Contains("forced target insert failure", exception.Message);
        var restoredSource = Assert.IsType<ContactDetail>(_repository.GetContactDetail(sourceContact));
        Assert.Null(restoredSource.Note);
        Assert.Null(restoredSource.CustomAvatarPath);
        Assert.Equal(2, restoredSource.Senders.Count);
        var restoredSender = restoredSource.Senders.Single(item => item.SenderId == sender);
        Assert.Equal(sender, restoredSender.SenderId);
        Assert.Equal("原标签", restoredSender.AccountLabel);
        Assert.True(restoredSender.IsPrimary);
        var restoredRemaining = restoredSource.Senders.Single(item => item.SenderId == remainingSender);
        Assert.Equal("保留标签", restoredRemaining.AccountLabel);
        Assert.False(restoredRemaining.IsPrimary);
        Assert.Empty(Assert.IsType<ContactDetail>(_repository.GetContactDetail(targetContact)).Senders);
        Assert.Equal(sourceContact, _repository.FindContactBySenderId(sender)!.Id);
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

        // Search literal % and _
        var c3 = _repository.CreateContact("Percent 100% User", note: "100%");
        var c4 = _repository.CreateContact("Percent 1000 User", note: "1000");
        var byPercent = _repository.ListContacts("100%");
        Assert.Single(byPercent);
        Assert.Equal(c3, byPercent[0].Id);
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

    [Fact]
    public void MergeContacts_TransfersSenderAndRemovesEmptyOldContact()
    {
        // Contact 1 (Alice on WeChat)
        var s1 = _archive.AddSender("wx_alice", "Alice WeChat", platform: "wechat");
        var c1 = _repository.CreateContact("Alice", initialBindings: new[] { (s1, (string?)null, true) });

        // Contact 2 (Alice on QQ)
        var s2 = _archive.AddSender("qq_alice", "Alice QQ", platform: "qq");
        var c2 = _repository.CreateContact("Alice QQ", initialBindings: new[] { (s2, (string?)null, true) });

        // Initially both contacts exist
        var initialList = _repository.ListContacts();
        Assert.Equal(2, initialList.Count);

        // Merge s2 into c1 (forceRebind = true)
        _repository.BindSender(c1, s2, accountLabel: "QQ大号", isPrimary: false, forceRebind: true);

        // Verify c1 now has both senders
        var c1Detail = _repository.GetContactDetail(c1);
        Assert.NotNull(c1Detail);
        Assert.Equal(2, c1Detail.Senders.Count);
        Assert.Contains(c1Detail.Senders, s => s.SenderId == s1);
        Assert.Contains(c1Detail.Senders, s => s.SenderId == s2 && s.AccountLabel == "QQ大号");

        // Verify c2 was automatically cleaned up (0 senders remaining)
        var c2Detail = _repository.GetContactDetail(c2);
        Assert.Null(c2Detail);

        // ListContacts should now show ONLY 1 merged contact!
        var updatedList = _repository.ListContacts();
        Assert.Single(updatedList);
        Assert.Equal(c1, updatedList[0].Id);
    }

    [Fact]
    public void ListAvailableSendersToBind_ReturnsAllBindableSendersWithCurrentBoundContact()
    {
        var s1 = _archive.AddSender("wx_user1", "User 1", platform: "wechat");
        var s2 = _archive.AddSender("qq_user2", "User 2", platform: "qq");
        var s3 = _archive.AddSender("wx_user3", "User 3", platform: "wechat");

        var c1 = _repository.CreateContact("Contact One", initialBindings: new[] { (s1, (string?)null, true) });
        var c2 = _repository.CreateContact("Contact Two", initialBindings: new[] { (s2, (string?)null, true) });

        // When c1 queries available senders to bind:
        // - s1 is already bound to c1 -> excluded
        // - s2 is bound to c2 -> included with BoundContactName = "Contact Two"
        // - s3 is unbound -> included with BoundContactName = null
        var available = _repository.ListAvailableSendersToBind(c1);
        Assert.Equal(2, available.Count);

        var s2Entry = available.FirstOrDefault(s => s.SenderId == s2);
        Assert.NotNull(s2Entry);
        Assert.Equal("Contact Two", s2Entry.BoundContactName);

        var s3Entry = available.FirstOrDefault(s => s.SenderId == s3);
        Assert.NotNull(s3Entry);
        Assert.Null(s3Entry.BoundContactName);
    }

    [Fact]
    public void ListAvailableSendersToBind_ExposesBoundContactNameAndExactId()
    {
        var boundSender = _archive.AddSender("wx_bound_owner", "Bound sender", platform: "wechat");
        var unboundSender = _archive.AddSender("wx_unbound_owner", "Unbound sender", platform: "wechat");
        var currentContact = _repository.CreateContact("Current contact");
        var boundContact = _repository.CreateContact(
            "Exact bound contact",
            initialBindings: [(boundSender, (string?)null, true)]);

        var available = _repository.ListAvailableSendersToBind(currentContact);

        var boundEntry = Assert.Single(available, item => item.SenderId == boundSender);
        Assert.Equal("Exact bound contact", boundEntry.BoundContactName);
        Assert.Equal(boundContact, boundEntry.BoundContactId);
        Assert.Equal(
            _repository.GetContactDetail(boundContact)!.IdentityToken,
            boundEntry.BoundContactIdentityToken);

        var unboundEntry = Assert.Single(available, item => item.SenderId == unboundSender);
        Assert.Null(unboundEntry.BoundContactName);
        Assert.Null(unboundEntry.BoundContactId);
        Assert.Null(unboundEntry.BoundContactIdentityToken);
    }

    [Fact]
    public void SenderDisplayName_Resolve_Handles_Null_And_Empty_Keys_Safely()
    {
        using var connection = _archive.Open();
        var emptyResult = SenderDisplayName.Resolve(connection, Array.Empty<(long, long?)>());
        Assert.Empty(emptyResult);

        var nullResult = SenderDisplayName.Resolve(connection, null);
        Assert.Empty(nullResult);
    }

    [Fact]
    public void ListUnboundSenders_HandlesMoreThan1000Senders_WithoutSqliteOverflow()
    {
        using (var connection = _archive.Open())
        {
            using var tx = connection.BeginTransaction();
            for (var i = 1; i <= 1050; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO senders(id, platform, account_id, native_id, current_name) VALUES (@id, 'wechat', 'acc', @native, @name)";
                cmd.Parameters.AddWithValue("@id", i + 10000);
                cmd.Parameters.AddWithValue("@native", $"wxid_{i}");
                cmd.Parameters.AddWithValue("@name", $"User_{i}");
                cmd.ExecuteNonQuery();

                using var aliasCmd = connection.CreateCommand();
                aliasCmd.Transaction = tx;
                aliasCmd.CommandText = "INSERT INTO sender_aliases(sender_id, alias) VALUES (@id, @alias)";
                aliasCmd.Parameters.AddWithValue("@id", i + 10000);
                aliasCmd.Parameters.AddWithValue("@alias", $"Alias_{i}");
                aliasCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var unbound = _repository.ListUnboundSenders();
        Assert.True(unbound.Count >= 1050);
    }
}


