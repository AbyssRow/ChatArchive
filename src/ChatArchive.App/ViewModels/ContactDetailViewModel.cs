using System.Collections.ObjectModel;
using ChatArchive.Core.IO;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatArchive.App.ViewModels;

public partial class ContactDetailViewModel : ObservableObject
{
    private readonly ContactRepository _contactRepository;
    private readonly AvatarStorageService? _avatarStorageService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private long _loadGeneration;

    public long ContactId { get; private set; }
    public string IdentityToken { get; private set; } = string.Empty;

    public ObservableCollection<BoundSenderInfo> BoundSenders { get; } = new();
    public ObservableCollection<SenderConversationInfo> Conversations { get; } = new();

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? CustomAvatarPath { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial long TotalMessageCount { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public ContactDetailViewModel(
        ContactRepository contactRepository,
        AvatarStorageService? avatarStorageService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
    {
        _contactRepository = contactRepository;
        _avatarStorageService = avatarStorageService;
        _dispatcher = dispatcher;
    }

    public async Task<bool> LoadAsync(long contactId)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        ContactId = contactId;
        IdentityToken = string.Empty;
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var detail = await Task.Run(() => _contactRepository.GetContactDetail(contactId));
            if (Volatile.Read(ref _loadGeneration) != generation)
            {
                return false;
            }

            if (detail is null)
            {
                ErrorMessage = $"未找到 ID 为 {contactId} 的联系人";
                return false;
            }

            void Apply()
            {
                if (Volatile.Read(ref _loadGeneration) != generation)
                {
                    return;
                }

                DisplayName = detail.DisplayName;
                IdentityToken = detail.IdentityToken;
                CustomAvatarPath = detail.CustomAvatarPath;
                Note = detail.Note;
                TotalMessageCount = detail.TotalMessageCount;

                BoundSenders.Clear();
                foreach (var sender in detail.Senders)
                {
                    BoundSenders.Add(sender);
                }

                Conversations.Clear();
                foreach (var conv in detail.Conversations)
                {
                    Conversations.Add(conv);
                }
            }

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource();
                var enqueued = _dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        Apply();
                        tcs.SetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                if (!enqueued)
                {
                    return false;
                }

                await tcs.Task;
            }
            else
            {
                Apply();
            }

            return true;
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _loadGeneration) == generation)
            {
                ErrorMessage = $"加载联系人详情失败：{ex.Message}";
            }
            return false;
        }
        finally
        {
            if (Volatile.Read(ref _loadGeneration) == generation)
            {
                IsLoading = false;
            }
        }
    }

    public async Task SaveBasicInfoAsync(
        string? displayName = null,
        string? note = null,
        string? customAvatarPath = null)
    {
        if (displayName != null)
        {
            DisplayName = displayName;
        }

        if (note != null)
        {
            Note = note;
        }

        if (customAvatarPath != null)
        {
            CustomAvatarPath = customAvatarPath;
        }

        await Task.Run(() => _contactRepository.UpdateContact(
            ContactId,
            DisplayName,
            CustomAvatarPath,
            Note));

        await LoadAsync(ContactId);
    }

    public async Task BindSenderAsync(
        long senderId,
        string? accountLabel = null,
        bool isPrimary = false,
        bool forceRebind = false)
    {
        await Task.Run(() => _contactRepository.BindSender(
            ContactId,
            senderId,
            accountLabel,
            isPrimary,
            forceRebind));

        await LoadAsync(ContactId);
    }

    public async Task TransferSenderFromExpectedContactAsync(
        long senderId,
        string expectedTargetIdentityToken,
        long expectedSourceContactId,
        string expectedSourceIdentityToken,
        string? accountLabel = null,
        bool isPrimary = false)
    {
        await Task.Run(() => _contactRepository.TransferSenderFromExpectedContact(
            ContactId,
            expectedTargetIdentityToken,
            senderId,
            expectedSourceContactId,
            expectedSourceIdentityToken,
            accountLabel,
            isPrimary));

        await LoadAsync(ContactId);
    }

    public async Task UnbindSenderAsync(long senderId)
    {
        await Task.Run(() => _contactRepository.UnbindSender(ContactId, senderId));
        await LoadAsync(ContactId);
    }

    public async Task UpdateAccountLabelAsync(long senderId, string? newLabel)
    {
        await Task.Run(() => _contactRepository.UpdateAccountLabel(ContactId, senderId, newLabel));
        await LoadAsync(ContactId);
    }

    public async Task SetPrimarySenderAsync(long senderId)
    {
        await Task.Run(() => _contactRepository.SetPrimarySender(ContactId, senderId));
        await LoadAsync(ContactId);
    }

    public async Task<string> SaveAvatarFromFileAsync(string sourceFilePath)
    {
        if (_avatarStorageService is null)
        {
            throw new InvalidOperationException("未配置 AvatarStorageService");
        }

        var savedPath = await Task.Run(() => _avatarStorageService.SaveAvatarFromFile(sourceFilePath));
        CustomAvatarPath = savedPath;

        await Task.Run(() => _contactRepository.UpdateContact(
            ContactId,
            DisplayName,
            CustomAvatarPath,
            Note));

        return savedPath;
    }

    public async Task<IReadOnlyList<BoundSenderInfo>> LoadAvailableSendersAsync(string? keyword = null)
    {
        return await Task.Run(() => _contactRepository.ListAvailableSendersToBind(ContactId, keyword));
    }
}
