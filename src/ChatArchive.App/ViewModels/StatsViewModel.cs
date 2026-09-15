using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

public partial class StatsViewModel : ObservableObject
{
    private readonly StatsRepository _repository;
    private readonly DispatcherQueue _dispatcher;
    private readonly LatestRequestGate _requestGate = new();
    private long _appliedGeneration;

    [ObservableProperty]
    public partial string SummaryLines { get; set; } = "加载中…";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public StatsViewModel(StatsRepository repository, DispatcherQueue dispatcher)
    {
        _repository = repository;
        _dispatcher = dispatcher;
    }

    public void Load()
    {
        var generation = _requestGate.Next();
        ErrorMessage = string.Empty;
        Task.Run(() => _repository.GetStats()).ContinueWith(t =>
        {
            string? summary = null;
            var error = string.Empty;
            if (t.IsCompletedSuccessfully)
            {
                var s = t.Result;
                summary =
                    $"消息总数 {Format(s.TotalMessages)}（QQ {Format(s.QQMessages)} / 微信 {Format(s.WeChatMessages)}）\n" +
                    $"会话 {Format(s.TotalConversations)}：私聊 {Format(s.PrivateConversations)}，群聊 {Format(s.GroupConversations)}\n" +
                    $"联系人 {Format(s.SenderCount)}\n" +
                    $"附件 {Format(s.AttachmentCount)}，可用 {Format(s.AvailableAttachments)}，缺失 {Format(s.MissingAttachments)}\n" +
                    $"媒体文件 {Format(s.MediaFileCount)}，共 {FormatBytes(s.MediaTotalBytes)}";
            }
            else
            {
                var message = t.Exception?.GetBaseException().Message ?? "未知错误";
                error = $"加载统计失败：{message}";
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (!_requestGate.IsCurrent(generation))
                {
                    return;
                }

                ApplyLoadResult(generation, summary ?? SummaryLines, error);
            });
        });
    }

    internal void ApplyLoadResult(long generation, string summary, string error)
    {
        if (generation < _appliedGeneration)
        {
            return;
        }

        _appliedGeneration = generation;
        SummaryLines = summary;
        ErrorMessage = error;
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
