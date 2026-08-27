using ChatArchive.Core.Models;

namespace ChatArchive.App.ViewModels;

/// <summary>搜索结果行包装：预格式化展示字段。</summary>
public sealed record SearchHitProxy(SearchHit Hit)
{
    public string PlatformLabel => Hit.Platform?.ToLowerInvariant() switch
    {
        "qq" => "QQ",
        "wechat" => "微信",
        "text" => "文本",
        "html" => "网页",
        "sql" => "SQL",
        _ => Hit.Platform ?? string.Empty,
    };

    public string TimeText => DateTimeOffset
        .FromUnixTimeMilliseconds(Hit.TimestampMs)
        .LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string Snippet => Hit.Snippet;
}
