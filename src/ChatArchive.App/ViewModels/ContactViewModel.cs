using System.Collections.ObjectModel;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatArchive.App.ViewModels;

public partial class ContactViewModel : ObservableObject
{
    private readonly SenderRepository _repository;

    public ObservableCollection<AliasInfo> Aliases { get; } = new();
    public ObservableCollection<SenderConversationInfo> Conversations { get; } = new();

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IdentityLine { get; set; } = string.Empty;

    public event Action<long>? ConversationActivated;

    /// <summary>资料弹窗里点击某个会话时调用。</summary>
    public void ActivateConversation(long conversationId)
    {
        ConversationActivated?.Invoke(conversationId);
    }

    public ContactViewModel(SenderRepository repository)
    {
        _repository = repository;
    }

    public async Task<bool> LoadAsync(long senderId)
    {
        var profile = await Task.Run(() => _repository.GetSender(senderId));
        if (profile is null)
        {
            return false;
        }

        DisplayName = profile.CurrentName;
        IdentityLine = (profile.Platform == "qq"
            ? $"QQ {profile.QQNumber ?? profile.NativeId}"
            : $"微信 {profile.NativeId}");
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
}


