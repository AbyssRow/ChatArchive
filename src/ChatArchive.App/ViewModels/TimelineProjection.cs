using ChatArchive.Core.Media;
using ChatArchive.Core.Models;
using System.Collections.ObjectModel;

namespace ChatArchive.App.ViewModels;

public sealed record AttachmentEntry(
    string Kind,
    string? Filename,
    string? ResolvedPath,
    bool IsImage,
    bool IsMissing)
{
    public string ActionText => Kind switch
    {
        "audio" or "voice" => "播放语音",
        "video" => "播放视频",
        "file" => "打开文件",
        _ => "打开附件",
    };

    public string MissingText => string.IsNullOrWhiteSpace(Filename)
        ? "媒体缺失"
        : $"{Filename}（文件缺失）";
}

public sealed record MessageProjection(
    string DisplayContent,
    IReadOnlyList<AttachmentEntry> Attachments)
{
    public IReadOnlyList<AttachmentEntry> Images => Attachments
        .Where(item => item.IsImage && !item.IsMissing)
        .ToList();

    public IReadOnlyList<AttachmentEntry> OpenableAttachments => Attachments
        .Where(item => !item.IsImage && !item.IsMissing)
        .ToList();

    public IReadOnlyList<AttachmentEntry> MissingAttachments => Attachments
        .Where(item => item.IsMissing)
        .ToList();
}

public static class TimelineProjection
{
    private static readonly HashSet<string> MediaMessageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "sticker", "emoji", "file", "video", "audio", "voice",
    };

    private static readonly string[] TechnicalPrefixes =
    {
        "[图片]", "[图片:", "[文件]", "[文件:", "[视频]", "[视频:",
        "[语音]", "[语音:", "[表情包]", "[表情包:", "[动画表情]", "[动画表情:",
        "[image]", "[image:", "[file]", "[file:", "[video]", "[video:",
        "[audio]", "[audio:", "[voice]", "[voice:", "[emoji]", "[emoji:",
    };

    public static MessageProjection ProjectMessage(MessageItem message, MediaLocator locator)
    {
        var attachments = message.Attachments
            .Select(attachment => ProjectAttachment(attachment, locator))
            .ToList();

        if (attachments.Count == 0 && IsMediaMessage(message))
        {
            attachments.Add(new AttachmentEntry(
                NormalizeKind(message.MediaType ?? message.MessageType),
                null,
                null,
                false,
                true));
        }

        var displayContent = IsMediaMessage(message)
                             && IsTechnicalContent(message.Content, message.Attachments)
            ? string.Empty
            : message.Content;
        return new MessageProjection(displayContent, attachments);
    }

    public static IReadOnlyList<TimelineEntry> BuildEntries(
        IEnumerable<MessageItem> messages,
        MediaLocator locator)
    {
        var entries = new List<TimelineEntry>();
        DateOnly? previousDate = null;
        foreach (var message in messages)
        {
            if (IsTimelineNoise(message))
            {
                continue;
            }

            var local = DateTimeOffset
                .FromUnixTimeMilliseconds(message.TimestampMs)
                .LocalDateTime;
            var date = DateOnly.FromDateTime(local);
            if (date != previousDate)
            {
                entries.Add(new DateSeparatorEntry(
                    date,
                    local.ToString("yyyy年M月d日 ddd")));
                previousDate = date;
            }

            entries.Add(new MessageEntry(message, locator));
        }

        return entries;
    }

    private static bool IsTimelineNoise(MessageItem message)
    {
        return message.IsSystem
            && string.Equals(message.Content.Trim(), "群聊更新", StringComparison.Ordinal);
    }

    public static void PrependOlder(
        ObservableCollection<TimelineEntry> target,
        IEnumerable<MessageItem> messages,
        MediaLocator locator)
    {
        var older = BuildEntries(messages, locator);
        if (older.Count == 0)
        {
            return;
        }

        var olderLastDate = older.OfType<DateSeparatorEntry>().Last().Date;
        if (target.FirstOrDefault() is DateSeparatorEntry existing
            && existing.Date == olderLastDate)
        {
            target.RemoveAt(0);
        }

        for (var index = older.Count - 1; index >= 0; index--)
        {
            target.Insert(0, older[index]);
        }
    }

    private static AttachmentEntry ProjectAttachment(AttachmentInfo attachment, MediaLocator locator)
    {
        var path = locator.Resolve(
            attachment.MediaSha256,
            attachment.ManagedPath,
            attachment.SourcePath);
        var kind = NormalizeKind(attachment.Kind);
        var isImage = kind is "image" or "sticker" or "emoji";
        return new AttachmentEntry(
            kind,
            attachment.Filename,
            path,
            isImage,
            path is null);
    }

    private static bool IsMediaMessage(MessageItem message)
    {
        return message.Attachments.Count > 0
            || !string.IsNullOrWhiteSpace(message.MediaType)
            || MediaMessageTypes.Contains(message.MessageType);
    }

    private static bool IsTechnicalContent(string content, IReadOnlyList<AttachmentInfo> attachments)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (TechnicalPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var normalized = NormalizePath(trimmed);
        foreach (var attachment in attachments)
        {
            foreach (var candidate in new[]
                     {
                         attachment.Filename,
                         attachment.DeclaredPath,
                         attachment.ManagedPath,
                         attachment.SourcePath,
                     })
            {
                if (!string.IsNullOrWhiteSpace(candidate)
                    && string.Equals(normalized, NormalizePath(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizePath(string value)
    {
        return value.Trim().Replace('\\', '/');
    }

    private static string NormalizeKind(string value)
    {
        return value.Equals("voice", StringComparison.OrdinalIgnoreCase)
            ? "audio"
            : value.ToLowerInvariant();
    }
}
