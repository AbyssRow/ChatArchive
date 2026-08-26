using System.Collections.ObjectModel;
using ChatArchive.Core.IO;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatArchive.App.ViewModels;

public partial class ContactViewModel : ObservableObject
{
    private readonly SenderRepository _repository;
    private readonly ContactRepository? _contactRepository;
    private readonly AvatarStorageService? _avatarStorageService;

    public long SenderId { get; private set; }

    public ObservableCollection<AliasInfo> Aliases { get; } = new();
    public ObservableCollection<SenderConversationInfo> Conversations { get; } = new();

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OriginalName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IdentityLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? CustomAvatarPath { get; set; }

    [ObservableProperty]
    public partial string? AccountLabel { get; set; }

    [ObservableProperty]
    public partial ContactInfo? BoundContact { get; set; }

    [ObservableProperty]
    public partial bool IsBound { get; set; }

    public event Action<long>? ConversationActivated;

    /// <summary>资料弹窗里点击某个会话时调用。</summary>
    public void ActivateConversation(long conversationId)
    {
        ConversationActivated?.Invoke(conversationId);
    }

    public ContactViewModel(SenderRepository repository)
        : this(repository, null, null)
    {
    }

    public ContactViewModel(
        SenderRepository repository,
        ContactRepository? contactRepository,
        AvatarStorageService? avatarStorageService = null)
    {
        _repository = repository;
        _contactRepository = contactRepository;
        _avatarStorageService = avatarStorageService;
    }

    public async Task<bool> LoadAsync(long senderId)
    {
        SenderId = senderId;
        var profile = await Task.Run(() => _repository.GetSender(senderId));
        if (profile is null)
        {
            return false;
        }

        OriginalName = profile.CurrentName;

        if (_contactRepository is not null)
        {
            var contact = await Task.Run(() => _contactRepository.FindContactBySenderId(senderId));
            BoundContact = contact;
            IsBound = contact is not null;

            if (contact is not null)
            {
                DisplayName = contact.DisplayName;
                CustomAvatarPath = contact.CustomAvatarPath;

                var detail = await Task.Run(() => _contactRepository.GetContactDetail(contact.Id));
                var boundSender = detail?.Senders.FirstOrDefault(s => s.SenderId == senderId);
                AccountLabel = boundSender?.AccountLabel;

                var platformName = profile.Platform == "qq" ? "QQ" : "微信";
                var idStr = profile.Platform == "qq"
                    ? (profile.QQNumber ?? profile.NativeId)
                    : profile.NativeId;

                IdentityLine = string.IsNullOrWhiteSpace(AccountLabel)
                    ? $"{platformName} {idStr}"
                    : $"{platformName} {AccountLabel} ({idStr})";
            }
            else
            {
                DisplayName = profile.CurrentName;
                CustomAvatarPath = null;
                AccountLabel = null;
                IdentityLine = (profile.Platform == "qq"
                    ? $"QQ {profile.QQNumber ?? profile.NativeId}"
                    : $"微信 {profile.NativeId}");
            }
        }
        else
        {
            BoundContact = null;
            IsBound = false;
            DisplayName = profile.CurrentName;
            CustomAvatarPath = null;
            AccountLabel = null;
            IdentityLine = (profile.Platform == "qq"
                ? $"QQ {profile.QQNumber ?? profile.NativeId}"
                : $"微信 {profile.NativeId}");
        }

        Aliases.Clear();
        foreach (var alias in profile.Aliases)
        {
            Aliases.Add(alias);
        }

        Conversations.Clear();
        foreach (var conversation in profile.Conversations)
        {
            Conversations.Add(conversation);
        }

        return true;
    }

    public async Task QuickUpdateContactNameAsync(string newDisplayName)
    {
        if (_contactRepository is null || BoundContact is null)
        {
            return;
        }

        await Task.Run(() => _contactRepository.UpdateContact(
            BoundContact.Id,
            newDisplayName,
            BoundContact.CustomAvatarPath,
            BoundContact.Note));

        await LoadAsync(SenderId);
    }

    public async Task<string?> QuickUpdateAvatarFromFileAsync(string sourceFilePath)
    {
        if (_avatarStorageService is null || _contactRepository is null || BoundContact is null)
        {
            return null;
        }

        var savedPath = await Task.Run(() => _avatarStorageService.SaveAvatarFromFile(sourceFilePath));

        await Task.Run(() => _contactRepository.UpdateContact(
            BoundContact.Id,
            DisplayName,
            savedPath,
            BoundContact.Note));

        await LoadAsync(SenderId);
        return savedPath;
    }

    public async Task QuickCreateAndBindContactAsync(
        string displayName,
        string? label = null,
        string? avatarPath = null,
        string? note = null)
    {
        if (_contactRepository is null)
        {
            return;
        }

        await Task.Run(() => _contactRepository.CreateContact(
            displayName,
            avatarPath,
            note,
            new[] { (SenderId, label, true) }));

        await LoadAsync(SenderId);
    }

    public async Task QuickBindToExistingContactAsync(
        long contactId,
        string? label = null,
        bool isPrimary = false,
        bool forceRebind = false)
    {
        if (_contactRepository is null)
        {
            return;
        }

        await Task.Run(() => _contactRepository.BindSender(
            contactId,
            SenderId,
            label,
            isPrimary,
            forceRebind));

        await LoadAsync(SenderId);
    }

    public async Task QuickUnbindContactAsync()
    {
        if (_contactRepository is null || BoundContact is null)
        {
            return;
        }

        await Task.Run(() => _contactRepository.UnbindSender(BoundContact.Id, SenderId));
        await LoadAsync(SenderId);
    }
}
