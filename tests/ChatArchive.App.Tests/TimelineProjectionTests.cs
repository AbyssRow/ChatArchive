using ChatArchive.App.ViewModels;
using ChatArchive.Core.Media;
using ChatArchive.Core.Models;
using System.Collections.ObjectModel;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class TimelineProjectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"chatarchive-app-tests-{Guid.NewGuid():N}");

    public TimelineProjectionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Available_attachment_hides_technical_content_and_keeps_real_caption()
    {
        var file = Path.Combine(_directory, "photo.jpg");
        File.WriteAllText(file, "image");
        var attachment = Attachment(
            kind: "image",
            filename: "photo.jpg",
            declaredPath: "MSG/images/photo.jpg",
            sourcePath: file);
        var locator = new MediaLocator(_directory);

        var technical = TimelineProjection.ProjectMessage(
            Message("image", "MSG/images/photo.jpg", attachment), locator);
        var caption = TimelineProjection.ProjectMessage(
            Message("image", "周末拍的照片", attachment), locator);

        Assert.Equal(string.Empty, technical.DisplayContent);
        Assert.True(Assert.Single(technical.Attachments).IsImage);
        Assert.False(technical.Attachments[0].IsMissing);
        Assert.Equal("周末拍的照片", caption.DisplayContent);
    }

    [Fact]
    public void Available_attachment_hides_colon_style_image_placeholder()
    {
        var file = Path.Combine(_directory, "6045FA2ADFAD1215757915BF886BC674.jpg");
        File.WriteAllText(file, "image");
        var attachment = Attachment(
            kind: "image",
            filename: Path.GetFileName(file),
            sourcePath: file);

        var result = TimelineProjection.ProjectMessage(
            Message("text", "[图片:6045FA2ADFAD1215757915BF886BC674.jpg]", attachment),
            new MediaLocator(_directory));

        Assert.Equal(string.Empty, result.DisplayContent);
        Assert.False(Assert.Single(result.Attachments).IsMissing);
    }

    [Fact]
    public void Colon_style_text_without_media_context_remains_visible()
    {
        var result = TimelineProjection.ProjectMessage(
            Message("text", "[图片:这只是普通文字]"),
            new MediaLocator(_directory));

        Assert.Equal("[图片:这只是普通文字]", result.DisplayContent);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public void Missing_and_implicit_media_are_reported()
    {
        var locator = new MediaLocator(_directory);
        var explicitMissing = TimelineProjection.ProjectMessage(
            Message("file", "[文件] report.pdf", Attachment("file", "report.pdf")), locator);
        var implicitMissing = TimelineProjection.ProjectMessage(
            Message("image", "[图片]"), locator);

        var explicitItem = Assert.Single(explicitMissing.Attachments);
        Assert.True(explicitItem.IsMissing);
        Assert.Equal("report.pdf（文件缺失）", explicitItem.MissingText);
        Assert.Equal(string.Empty, explicitMissing.DisplayContent);

        var implicitItem = Assert.Single(implicitMissing.Attachments);
        Assert.True(implicitItem.IsMissing);
        Assert.Equal("媒体缺失", implicitItem.MissingText);
        Assert.Equal(string.Empty, implicitMissing.DisplayContent);
    }

    [Fact]
    public void Multiple_available_attachments_are_all_projected()
    {
        var image = Path.Combine(_directory, "one.png");
        var file = Path.Combine(_directory, "two.zip");
        File.WriteAllText(image, "image");
        File.WriteAllText(file, "file");
        var message = Message(
            "file",
            "附件见下",
            Attachment("image", "one.png", sourcePath: image, ordinal: 0),
            Attachment("file", "two.zip", sourcePath: file, ordinal: 1));

        var result = TimelineProjection.ProjectMessage(message, new MediaLocator(_directory));

        Assert.Equal(2, result.Attachments.Count);
        Assert.Single(result.Images);
        Assert.Single(result.OpenableAttachments);
        Assert.Equal("附件见下", result.DisplayContent);
    }

    [Fact]
    public void Older_page_is_prepended_and_same_day_separator_is_not_duplicated()
    {
        var locator = new MediaLocator(_directory);
        var current = new ObservableCollection<TimelineEntry>(TimelineProjection.BuildEntries(
            new[]
            {
                Message(3, LocalTimestamp(2026, 8, 20, 11), "较新"),
                Message(4, LocalTimestamp(2026, 8, 20, 12), "最新"),
            },
            locator));

        TimelineProjection.PrependOlder(
            current,
            new[]
            {
                Message(1, LocalTimestamp(2026, 8, 19, 23), "最旧"),
                Message(2, LocalTimestamp(2026, 8, 20, 10), "较旧"),
            },
            locator);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, current.OfType<MessageEntry>().Select(e => e.Message.Id));
        var separators = current.OfType<DateSeparatorEntry>().ToList();
        Assert.Equal(2, separators.Count);
        Assert.Contains("8月19日", separators[0].Label);
        Assert.Contains("8月20日", separators[1].Label);
    }

    [Fact]
    public void Message_time_contains_only_local_time()
    {
        var entry = Assert.IsType<MessageEntry>(TimelineProjection.BuildEntries(
            new[] { Message(1, LocalTimestamp(2026, 8, 20, 9, 8, 7), "消息") },
            new MediaLocator(_directory))[1]);

        Assert.Equal("09:08:07", entry.TimeText);
    }

    [Fact]
    public void Exact_system_group_update_is_omitted_from_timeline()
    {
        var messages = new[]
        {
            Message(1, LocalTimestamp(2026, 8, 20, 9), "之前"),
            new MessageItem(
                2, 1, null, "系统", "system", "system", null,
                "群聊更新", false, true, LocalTimestamp(2026, 8, 20, 10),
                Array.Empty<AttachmentInfo>()),
            Message(3, LocalTimestamp(2026, 8, 20, 11), "之后"),
        };

        var entries = TimelineProjection.BuildEntries(messages, new MediaLocator(_directory));

        Assert.Equal(new long[] { 1, 3 }, entries.OfType<MessageEntry>().Select(entry => entry.Message.Id));
        Assert.Single(entries.OfType<DateSeparatorEntry>());
    }

    [Fact]
    public void MessageEntry_projects_custom_avatar_and_account_badge_when_present()
    {
        var locator = new MediaLocator(_directory);
        var message = new MessageItem(
            1, 1, 100, "张总", "incoming", "text", null,
            "你好", false, false, LocalTimestamp(2026, 8, 20, 10),
            Array.Empty<AttachmentInfo>(),
            CustomAvatarPath: "C:/avatars/zhang.png",
            AccountLabel: "工作号");

        var entry = new MessageEntry(message, locator);

        Assert.Equal("C:/avatars/zhang.png", entry.AvatarPath);
        Assert.Equal("工作号", entry.AccountBadge);
        Assert.Equal("张总 · 工作号", entry.DisplaySenderName);
        Assert.Equal("张", entry.Initials);
    }

    [Fact]
    public void MessageEntry_handles_missing_or_whitespace_account_label_and_avatar()
    {
        var locator = new MediaLocator(_directory);
        var message = new MessageItem(
            1, 1, 100, "李四", "incoming", "text", null,
            "收到", false, false, LocalTimestamp(2026, 8, 20, 10),
            Array.Empty<AttachmentInfo>(),
            CustomAvatarPath: null,
            AccountLabel: "   ");

        var entry = new MessageEntry(message, locator);

        Assert.Null(entry.AvatarPath);
        Assert.Null(entry.AccountBadge);
        Assert.Equal("李四", entry.DisplaySenderName);
        Assert.Equal("李", entry.Initials);
    }

    [Fact]
    public void MessageEntry_handles_empty_sender_name_initials()
    {
        var locator = new MediaLocator(_directory);
        var message = new MessageItem(
            1, 1, 100, "   ", "incoming", "text", null,
            "无名消息", false, false, LocalTimestamp(2026, 8, 20, 10),
            Array.Empty<AttachmentInfo>());

        var entry = new MessageEntry(message, locator);

        Assert.Equal("?", entry.Initials);
        Assert.Equal("   ", entry.DisplaySenderName);
    }

    private static MessageItem Message(string type, string content, params AttachmentInfo[] attachments)
    {
        return Message(1, 1_700_000_000_000, content, type, attachments);
    }

    private static MessageItem Message(
        long id,
        long timestampMs,
        string content,
        string type = "text",
        params AttachmentInfo[] attachments)
    {
        return new MessageItem(
            id, 1, null, "Alice", "incoming", type, type == "text" ? null : type,
            content, false, false, timestampMs, attachments);
    }

    private static AttachmentInfo Attachment(
        string kind,
        string? filename,
        string? declaredPath = null,
        string? sourcePath = null,
        int ordinal = 0)
    {
        return new AttachmentInfo(
            ordinal + 1, ordinal, kind, filename, sourcePath is not null,
            null, null, null, null, declaredPath, null, sourcePath, null);
    }

    private static long LocalTimestamp(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0,
        int second = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
