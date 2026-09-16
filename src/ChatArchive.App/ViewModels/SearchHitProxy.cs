using ChatArchive.Core.Models;

namespace ChatArchive.App.ViewModels;

public sealed record SearchHitProxy(SearchHit Hit)
{
    public string PlatformLabel => UiInputParser.PlatformLabel(Hit.Platform);

    public string TimeText => DateTimeOffset
        .FromUnixTimeMilliseconds(Math.Clamp(Hit.TimestampMs, 0, 253402300799000L))
        .LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string Snippet => Hit.Snippet;
}
