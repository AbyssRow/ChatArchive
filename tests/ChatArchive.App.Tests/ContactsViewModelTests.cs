using ChatArchive.App.ViewModels;
using ChatArchive.Core.Data;
using ChatArchive.Core.IO;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class ContactsViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _avatarDirectory;
    private readonly ArchiveDatabase _database;
    private readonly ContactRepository _contactRepository;
    private readonly SenderRepository _senderRepository;
    private readonly AvatarStorageService _avatarStorage;

    public ContactsViewModelTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"chatarchive-vm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _avatarDirectory = Path.Combine(_tempDirectory, "avatars");
        Directory.CreateDirectory(_avatarDirectory);

        var dbPath = Path.Combine(_tempDirectory, "test.db");
        _database = new ArchiveDatabase(dbPath);
        _database.EnsureSchema();

        _contactRepository = new ContactRepository(_database);
        _senderRepository = new SenderRepository(_database);
        _avatarStorage = new AvatarStorageService(_avatarDirectory);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private long InsertSender(string nativeId, string currentName, string platform = "qq")
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO senders(platform, account_id, native_id, current_name, is_self)
            VALUES (@platform, 'acc', @native, @name, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@native", nativeId);
        cmd.Parameters.AddWithValue("@name", currentName);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long InsertConversation(string nativeId, string title, string platform = "qq")
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations(platform, account_id, native_id, kind, title, message_count)
            VALUES (@platform, 'acc', @native, 'private', @title, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@native", nativeId);
        cmd.Parameters.AddWithValue("@title", title);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void InsertMessage(long convId, long senderId, string content, long timestampMs = 1700000000000)
    {
        using var conn = _database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages(
                conversation_id, sender_id, platform, timestamp_ms, direction, message_type,
                content, search_text, sender_name_snapshot, conversation_title_snapshot,
                is_recalled, is_system, payload_hash, semantic_hash, raw_payload_json)
            VALUES (
                @conv, @sender, 'qq', @ts, 'incoming', 'text',
                @content, @content, 'Sender', 'Title', 0, 0,
                @hash, @hash, '{}');
            """;
        cmd.Parameters.AddWithValue("@conv", convId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        cmd.Parameters.AddWithValue("@ts", timestampMs);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@hash", Guid.NewGuid().ToString("N"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task ContactsViewModel_LoadAsync_PopulatesContactsList()
    {
        var s1 = InsertSender("10001", "张三QQ");
        _contactRepository.CreateContact("张三", note: "好友", initialBindings: [(s1, "大号", true)]);
        _contactRepository.CreateContact("李四", note: "同事");

        var viewModel = new ContactsViewModel(_contactRepository, _avatarStorage);
        Assert.Empty(viewModel.Contacts);

        await viewModel.LoadAsync();

        Assert.Equal(2, viewModel.Contacts.Count);
        Assert.Contains(viewModel.Contacts, c => c.DisplayName == "张三");
        Assert.Contains(viewModel.Contacts, c => c.DisplayName == "李四");
    }

    [Fact]
    public async Task ContactsViewModel_SearchKeyword_FiltersContacts()
    {
        var s1 = InsertSender("10001", "张三QQ");
        var s2 = InsertSender("wxid_lisi", "李四微信", platform: "wechat");
        var s3 = InsertSender("10003", "王五QQ");

        _contactRepository.CreateContact("张三", initialBindings: [(s1, "大号", true)]);
        _contactRepository.CreateContact("李四", initialBindings: [(s2, "工作号", true)]);
        _contactRepository.CreateContact("王五", initialBindings: [(s3, null, true)]);

        var viewModel = new ContactsViewModel(_contactRepository, _avatarStorage);

        viewModel.SearchKeyword = "张三";
        await viewModel.LoadAsync();
        Assert.Single(viewModel.Contacts);
        Assert.Equal("张三", viewModel.Contacts[0].DisplayName);

        viewModel.SearchKeyword = "工作号";
        await viewModel.LoadAsync();
        Assert.Single(viewModel.Contacts);
        Assert.Equal("李四", viewModel.Contacts[0].DisplayName);
    }

    [Fact]
    public async Task ContactsViewModel_SelectContactAsync_LoadsSelectedDetail()
    {
        var s1 = InsertSender("10001", "张三QQ");
        var cId = _contactRepository.CreateContact("张三", "avatars/zhang.png", "大学同学", [(s1, "大号", true)]);
        var convId = InsertConversation("conv_zhang", "张三的聊天");
        InsertMessage(convId, s1, "你好啊");

        var viewModel = new ContactsViewModel(_contactRepository, _avatarStorage);
        await viewModel.LoadAsync();

        var contactInfo = viewModel.Contacts.First(c => c.Id == cId);
        await viewModel.SelectContactAsync(contactInfo);

        Assert.NotNull(viewModel.SelectedContact);
        Assert.Equal(cId, viewModel.SelectedContact.Id);

        Assert.NotNull(viewModel.SelectedDetail);
        Assert.Equal("张三", viewModel.SelectedDetail.DisplayName);
        Assert.Equal("avatars/zhang.png", viewModel.SelectedDetail.CustomAvatarPath);
        Assert.Equal("大学同学", viewModel.SelectedDetail.Note);
        Assert.Single(viewModel.SelectedDetail.BoundSenders);
        Assert.Equal("大号", viewModel.SelectedDetail.BoundSenders[0].AccountLabel);
        Assert.Single(viewModel.SelectedDetail.Conversations);
        Assert.Equal("张三的聊天", viewModel.SelectedDetail.Conversations[0].Title);
    }

    [Fact]
    public async Task ContactsViewModel_CreateNewContactAsync_CreatesAndSelectsContact()
    {
        var s1 = InsertSender("10002", "王小二QQ");
        var viewModel = new ContactsViewModel(_contactRepository, _avatarStorage);
        await viewModel.LoadAsync();
        Assert.Empty(viewModel.Contacts);

        var detail = await viewModel.CreateNewContactAsync(
            "王小二",
            note: "新添加的联系人",
            initialBindings: [(s1, "主账号", true)]);

        Assert.NotNull(detail);
        Assert.Equal("王小二", detail.DisplayName);
        Assert.Equal("新添加的联系人", detail.Note);
        Assert.Single(detail.BoundSenders);
        Assert.Equal("主账号", detail.BoundSenders[0].AccountLabel);

        Assert.Single(viewModel.Contacts);
        Assert.Equal("王小二", viewModel.Contacts[0].DisplayName);
        Assert.NotNull(viewModel.SelectedContact);
        Assert.Equal(viewModel.Contacts[0].Id, viewModel.SelectedContact.Id);
    }

    [Fact]
    public async Task ContactsViewModel_DeleteContactAsync_RemovesContactAndClearsSelection()
    {
        var cId = _contactRepository.CreateContact("将被删除", note: "待删");
        var viewModel = new ContactsViewModel(_contactRepository, _avatarStorage);
        await viewModel.LoadAsync();
        Assert.Single(viewModel.Contacts);

        await viewModel.SelectContactAsync(viewModel.Contacts[0]);
        Assert.NotNull(viewModel.SelectedDetail);

        await viewModel.DeleteContactAsync(cId);

        Assert.Empty(viewModel.Contacts);
        Assert.Null(viewModel.SelectedContact);
        Assert.Null(viewModel.SelectedDetail);
        Assert.Null(_contactRepository.GetContactDetail(cId));
    }

    [Fact]
    public async Task ContactDetailViewModel_SaveBasicInfoAsync_UpdatesContactData()
    {
        var cId = _contactRepository.CreateContact("原始名字", note: "旧笔记");
        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(cId);

        Assert.Equal("原始名字", detailVm.DisplayName);
        Assert.Equal("旧笔记", detailVm.Note);

        await detailVm.SaveBasicInfoAsync("新名字", "新笔记内容", "avatars/new.png");

        Assert.Equal("新名字", detailVm.DisplayName);
        Assert.Equal("新笔记内容", detailVm.Note);
        Assert.Equal("avatars/new.png", detailVm.CustomAvatarPath);

        var reloaded = _contactRepository.GetContactDetail(cId);
        Assert.NotNull(reloaded);
        Assert.Equal("新名字", reloaded.DisplayName);
        Assert.Equal("新笔记内容", reloaded.Note);
        Assert.Equal("avatars/new.png", reloaded.CustomAvatarPath);
    }

    [Fact]
    public async Task ContactDetailViewModel_BindSenderAsync_And_UnbindSenderAsync_WorkCorrectly()
    {
        var s1 = InsertSender("10001", "账号1");
        var s2 = InsertSender("wxid_2", "账号2", platform: "wechat");
        var cId = _contactRepository.CreateContact("测试绑定");

        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(cId);
        Assert.Empty(detailVm.BoundSenders);

        await detailVm.BindSenderAsync(s1, accountLabel: "QQ大号", isPrimary: true);
        await detailVm.BindSenderAsync(s2, accountLabel: "微信小号", isPrimary: false);

        Assert.Equal(2, detailVm.BoundSenders.Count);
        var first = detailVm.BoundSenders.First(s => s.SenderId == s1);
        Assert.Equal("QQ大号", first.AccountLabel);
        Assert.True(first.IsPrimary);

        await detailVm.UnbindSenderAsync(s1);
        Assert.Single(detailVm.BoundSenders);
        Assert.Equal(s2, detailVm.BoundSenders[0].SenderId);
    }

    [Fact]
    public async Task ContactDetailViewModel_BindSenderAsync_does_not_transfer_without_explicit_force()
    {
        var senderId = InsertSender("10086", "已有归属账号");
        var oldContactId = _contactRepository.CreateContact(
            "旧联系人",
            initialBindings: [(senderId, "原账号", true)]);
        var newContactId = _contactRepository.CreateContact("新联系人");
        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(newContactId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => detailVm.BindSenderAsync(senderId));

        Assert.Contains(
            _contactRepository.GetContactDetail(oldContactId)!.Senders,
            sender => sender.SenderId == senderId);
        Assert.Empty(_contactRepository.GetContactDetail(newContactId)!.Senders);
    }

    [Fact]
    public async Task ContactDetailViewModel_UpdateAccountLabelAsync_UpdatesLabel()
    {
        var s1 = InsertSender("10001", "账号1");
        var cId = _contactRepository.CreateContact("测试标签", initialBindings: [(s1, "旧标签", true)]);

        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(cId);
        Assert.Equal("旧标签", detailVm.BoundSenders[0].AccountLabel);

        await detailVm.UpdateAccountLabelAsync(s1, "新身份标签");
        Assert.Equal("新身份标签", detailVm.BoundSenders[0].AccountLabel);
    }

    [Fact]
    public async Task ContactDetailViewModel_SetPrimarySenderAsync_SwitchesPrimary()
    {
        var s1 = InsertSender("10001", "账号1");
        var s2 = InsertSender("10002", "账号2");
        var cId = _contactRepository.CreateContact("主号切换", initialBindings: [
            (s1, "号1", true),
            (s2, "号2", false)
        ]);

        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(cId);

        Assert.True(detailVm.BoundSenders.First(s => s.SenderId == s1).IsPrimary);
        Assert.False(detailVm.BoundSenders.First(s => s.SenderId == s2).IsPrimary);

        await detailVm.SetPrimarySenderAsync(s2);

        Assert.False(detailVm.BoundSenders.First(s => s.SenderId == s1).IsPrimary);
        Assert.True(detailVm.BoundSenders.First(s => s.SenderId == s2).IsPrimary);
    }

    [Fact]
    public async Task ContactDetailViewModel_SaveAvatarFromFileAsync_StoresAvatarAndUpdatesContact()
    {
        var tempImage = Path.Combine(_tempDirectory, "sample_avatar.png");
        File.WriteAllBytes(tempImage, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 });

        var cId = _contactRepository.CreateContact("头像测试");
        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(cId);
        Assert.Null(detailVm.CustomAvatarPath);

        var savedPath = await detailVm.SaveAvatarFromFileAsync(tempImage);

        Assert.NotNull(savedPath);
        Assert.EndsWith(".png", savedPath);
        Assert.Equal(savedPath, detailVm.CustomAvatarPath);

        var resolvedFull = _avatarStorage.ResolveAvatarFullPath(savedPath);
        Assert.NotNull(resolvedFull);
        Assert.True(File.Exists(resolvedFull));
    }

    [Fact]
    public async Task ContactDetailViewModel_LoadAvailableSendersAsync_ReturnsOnlyUnboundSenders()
    {
        var s1 = InsertSender("10001", "已绑定Sender");
        var s2 = InsertSender("10002", "未绑定Sender1");
        var s3 = InsertSender("wxid_3", "未绑定Sender2", platform: "wechat");

        var cId = _contactRepository.CreateContact("测试可用Sender", initialBindings: [(s1, "主", true)]);
        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
        await detailVm.LoadAsync(cId);

        var available = await detailVm.LoadAvailableSendersAsync();

        Assert.Equal(2, available.Count);
        Assert.DoesNotContain(available, s => s.SenderId == s1);
        Assert.Contains(available, s => s.SenderId == s2);
        Assert.Contains(available, s => s.SenderId == s3);
    }

    [Fact]
    public async Task ContactViewModel_UnboundSender_LoadsCorrectly()
    {
        var s1 = InsertSender("10001", "路人甲");
        var convId = InsertConversation("conv1", "私聊");
        InsertMessage(convId, s1, "你好");

        var contactVm = new ContactViewModel(_senderRepository, _contactRepository, _avatarStorage);
        var loaded = await contactVm.LoadAsync(s1);

        Assert.True(loaded);
        Assert.False(contactVm.IsBound);
        Assert.Null(contactVm.BoundContact);
        Assert.Equal("路人甲", contactVm.DisplayName);
        Assert.Equal("QQ 10001", contactVm.IdentityLine);
        Assert.Null(contactVm.CustomAvatarPath);
    }

    [Fact]
    public async Task ContactViewModel_BoundSender_LoadsBoundContactInfo()
    {
        var s1 = InsertSender("10001", "路人甲");
        var cId = _contactRepository.CreateContact("张总", "avatars/boss.png", "公司老板", [(s1, "工作号", true)]);

        var contactVm = new ContactViewModel(_senderRepository, _contactRepository, _avatarStorage);
        var loaded = await contactVm.LoadAsync(s1);

        Assert.True(loaded);
        Assert.True(contactVm.IsBound);
        Assert.NotNull(contactVm.BoundContact);
        Assert.Equal(cId, contactVm.BoundContact.Id);
        Assert.Equal("张总", contactVm.DisplayName);
        Assert.Equal("avatars/boss.png", contactVm.CustomAvatarPath);
        Assert.Equal("工作号", contactVm.AccountLabel);
        Assert.Contains("工作号", contactVm.IdentityLine);
    }

    [Fact]
    public async Task ContactViewModel_QuickActions_CreateBindUnbindAndRename()
    {
        var s1 = InsertSender("10001", "小明原始名");
        var contactVm = new ContactViewModel(_senderRepository, _contactRepository, _avatarStorage);
        await contactVm.LoadAsync(s1);

        Assert.False(contactVm.IsBound);

        // 1. 快速创建并绑定
        await contactVm.QuickCreateAndBindContactAsync("小明", "大号", note: "好友小明");
        Assert.True(contactVm.IsBound);
        Assert.NotNull(contactVm.BoundContact);
        Assert.Equal("小明", contactVm.DisplayName);
        Assert.Equal("大号", contactVm.AccountLabel);

        // 2. 快速改名
        await contactVm.QuickUpdateContactNameAsync("小明同学");
        Assert.Equal("小明同学", contactVm.DisplayName);
        var currentContactId = contactVm.BoundContact.Id;

        // 3. 快速设置头像
        var tempImage = Path.Combine(_tempDirectory, "avatar_quick.jpg");
        File.WriteAllBytes(tempImage, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        var savedAvatar = await contactVm.QuickUpdateAvatarFromFileAsync(tempImage);
        Assert.NotNull(savedAvatar);
        Assert.Equal(savedAvatar, contactVm.CustomAvatarPath);

        // 4. 快速解绑
        await contactVm.QuickUnbindContactAsync();
        Assert.False(contactVm.IsBound);
        Assert.Null(contactVm.BoundContact);
        Assert.Equal("小明原始名", contactVm.DisplayName);

        // 5. 快速绑定到已有联系人
        await contactVm.QuickBindToExistingContactAsync(currentContactId, "小号", isPrimary: false);
        Assert.True(contactVm.IsBound);
        Assert.NotNull(contactVm.BoundContact);
        Assert.Equal(currentContactId, contactVm.BoundContact.Id);
        Assert.Equal("小明同学", contactVm.DisplayName);
        Assert.Equal("小号", contactVm.AccountLabel);
    }

    [Fact]
    public async Task LoadAsync_WithPreferredSelectedContactId_PreservesSelectedContactAndDetail()
    {
        var s1 = InsertSender("wx_1", "User 1", platform: "wechat");
        var s2 = InsertSender("wx_2", "User 2", platform: "wechat");

        var c1 = _contactRepository.CreateContact("Contact 1", initialBindings: new[] { (s1, (string?)null, true) });
        var c2 = _contactRepository.CreateContact("Contact 2", initialBindings: new[] { (s2, (string?)null, true) });

        var vm = new ContactsViewModel(_contactRepository, _avatarStorage);
        await vm.LoadAsync();
        Assert.Equal(2, vm.Contacts.Count);

        // Select Contact 2
        var contact2 = vm.Contacts.First(c => c.Id == c2);
        await vm.SelectContactAsync(contact2);
        Assert.NotNull(vm.SelectedContact);
        Assert.NotNull(vm.SelectedDetail);
        Assert.Equal(c2, vm.SelectedContact.Id);

        // Modify Contact 2 name in repository
        _contactRepository.UpdateContact(c2, "Contact 2 Updated", null, "Some note");

        // Reload contacts specifying preferred contact id
        await vm.LoadAsync(preferredSelectedContactId: c2);

        // Verify selection is preserved with new details
        Assert.NotNull(vm.SelectedContact);
        Assert.NotNull(vm.SelectedDetail);
        Assert.Equal(c2, vm.SelectedContact.Id);
        Assert.Equal("Contact 2 Updated", vm.SelectedContact.DisplayName);
        Assert.Equal("Contact 2 Updated", vm.SelectedDetail.DisplayName);
    }

    [Fact]
    public async Task DeleteContactAsync_DoesNotRecreateDeletedContact()
    {
        var s1 = InsertSender("wx_del", "Deleted User", platform: "wechat");
        var conv1 = InsertConversation("wx_del", "Deleted User Chat", platform: "wechat");
        InsertMessage(conv1, s1, "Hello from private sender");

        var vm = new ContactsViewModel(_contactRepository, _avatarStorage);
        // Load initially with autoPopulate = true
        await vm.LoadAsync(autoPopulate: true);
        Assert.Single(vm.Contacts);
        var contactId = vm.Contacts[0].Id;

        // Delete the contact
        await vm.DeleteContactAsync(contactId);

        // Contacts list should be empty and selection cleared
        Assert.Empty(vm.Contacts);
        Assert.Null(vm.SelectedContact);
        Assert.Null(vm.SelectedDetail);

        // Calling LoadAsync() without autoPopulate does not recreate the deleted contact
        await vm.LoadAsync();
        Assert.Empty(vm.Contacts);
    }

    [Fact]
    public async Task SelectContactAsync_GenerationGuard_PreventsStaleDetailOverwrite()
    {
        var s1 = InsertSender("10001", "Contact 1 QQ");
        var s2 = InsertSender("10002", "Contact 2 QQ");
        var c1 = _contactRepository.CreateContact("Contact 1", initialBindings: [(s1, "号1", true)]);
        var c2 = _contactRepository.CreateContact("Contact 2", initialBindings: [(s2, "号2", true)]);

        var vm = new ContactsViewModel(_contactRepository, _avatarStorage);
        await vm.LoadAsync();
        Assert.Equal(2, vm.Contacts.Count);

        var contact1 = vm.Contacts.First(c => c.Id == c1);
        var contact2 = vm.Contacts.First(c => c.Id == c2);

        // Start selecting contact1, then immediately select contact2
        var t1 = vm.SelectContactAsync(contact1);
        var t2 = vm.SelectContactAsync(contact2);

        await Task.WhenAll(t1, t2);

        Assert.Equal(c2, vm.SelectedContact?.Id);
        Assert.NotNull(vm.SelectedDetail);
        Assert.Equal("Contact 2", vm.SelectedDetail.DisplayName);
    }
}

