using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

public partial class StatsViewModel : ObservableObject
{
    private readonly StatsRepository _repository;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string SummaryLines { get; set; } = "加载中…";

    public StatsViewModel(StatsRepository repository, DispatcherQueue dispatcher)
    {
        _repository = repository;
        _dispatcher = dispatcher;
    }

    public void Load()
    {
        Task.Run(() => _repository.GetStats()).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                var s = t.Result;
                var text =
                    $"消息总数 {Format(s.TotalMessages)}（QQ {Format(s.QQMessages)} / 微信 {Format(s.WeChatMessages)}）\n" +
                    $"会话 {Format(s.TotalConversations)}：私聊 {Format(s.PrivateConversations)}，群聊 {Format(s.GroupConversations)}\n" +
                    $"联系人 {Format(s.SenderCount)}\n" +
                    $"附件 {Format(s.AttachmentCount)}，可用 {Format(s.AvailableAttachments)}，缺失 {Format(s.MissingAttachments)}\n" +
                    $"媒体文件 {Format(s.MediaFileCount)}，共 {FormatBytes(s.MediaTotalBytes)}";
                _dispatcher.TryEnqueue(() => SummaryLines = text);
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
