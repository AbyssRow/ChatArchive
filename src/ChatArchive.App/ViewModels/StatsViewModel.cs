using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatArchive.App.ViewModels;

public partial class StatsViewModel : ObservableObject
{
    private readonly StatsRepository _repository;

    [ObservableProperty]
    public partial string SummaryLines { get; set; } = "加载中…";

    public StatsViewModel(StatsRepository repository)
    {
        _repository = repository;
    }

    public void Load()
    {
        Task.Run(() => _repository.GetStats()).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                var s = t.Result;
                SummaryLines =
                    $"消息总数 {Format(s.TotalMessages)}（QQ {Format(s.QQMessages)} / 微信 {Format(s.WeChatMessages)}）\n" +
                    $"会话 {Format(s.TotalConversations)}：私聊 {Format(s.PrivateConversations)}，群聊 {Format(s.GroupConversations)}\n" +
                    $"联系人 {Format(s.SenderCount)}\n" +
                    $"附件 {Format(s.AttachmentCount)}，可用 {Format(s.AvailableAttachments)}，缺失 {Format(s.MissingAttachments)}\n" +
                    $"媒体文件 {Format(s.MediaFileCount)}，共 {FormatBytes(s.MediaTotalBytes)}";
            }
        });
    }

    internal static string Format(long value)
    {
        return value.ToString("N0");
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
        {
            return $"{bytes / (double)(1L << 30):F2} GiB";
        }
        if (bytes >= 1L << 20)
        {
            return $"{bytes / (double)(1L << 20):F1} MiB";
        }
        if (bytes >= 1L << 10)
        {
            return $"{bytes / (double)(1L << 10):F1} KiB";
        }
        return $"{bytes} B";
    }
}
