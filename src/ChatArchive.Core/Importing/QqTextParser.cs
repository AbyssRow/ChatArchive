using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

/// <summary>Parser for the current QQ Chat Exporter V5 TXT output.</summary>
public static class QqTextParser
{
    private const string Signature =
        "[QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]";

    private static readonly Regex NumberRegex = new(@"^\[(?<number>\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"^时间:\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex TypeRegex = new(@"^类型:\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex ContentRegex = new(@"^内容:\s*(?<value>.*)$", RegexOptions.Compiled);
    private static readonly Regex ResourceRegex = new(
        @"^\s{2}-\s+(?<type>[^:：]+)[:：]\s*(?<name>.*)$", RegexOptions.Compiled);
    private static readonly Regex ResourceCountRegex = new(@"^资源:\s*(?<count>\d+)\s+个文件\s*$", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(@"^聊天名称:\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex ChatTypeRegex = new(@"^聊天类型:\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex SenderRegex = new(@"^(?<value>.+):$", RegexOptions.Compiled);
    private static readonly Regex SenderTitleRegex = new(@"^\[(?<title>[^\]]+)\]\s+(?<sender>.+)$", RegexOptions.Compiled);

    public static bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public static bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            _ = ReadHeader(filePath, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportFormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public static ParsedConversation ReadConversation(string filePath, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(filePath, cancellationToken);
        return new ParsedConversation(
            "qq",
            "qq-default",
            ImportText.StableFileNativeId(filePath),
            header.Kind,
            header.Title);
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var current = default(MessageBlock);
        var messageIndex = 0;
        var hasValidMessage = false;
        var pendingFooterSeparator = false;
        var lineNumber = 0;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            if (current?.PendingResources is { } pendingResources)
            {
                var pendingResource = ResourceRegex.Match(line);
                if (pendingResources.Resources.Count < pendingResources.DeclaredCount && pendingResource.Success)
                {
                    pendingResources.Resources.Add(new ResourceLine(
                        pendingResource.Groups["type"].Value.Trim(),
                        pendingResource.Groups["name"].Value.Trim()));
                    pendingResources.Lines.Add(line);
                    continue;
                }

                if (pendingResources.Resources.Count == pendingResources.DeclaredCount && !pendingResource.Success)
                {
                    CommitResourceCandidate(current, pendingResources);
                }
                else
                {
                    ReplayResourceCandidate(current, pendingResources);
                }
            }

            if (pendingFooterSeparator)
            {
                pendingFooterSeparator = false;
                if (line.Trim() == "导出完成")
                {
                    if (current is not null)
                    {
                        yield return BuildMessage(filePath, conversation, messageIndex++, current);
                        hasValidMessage = true;
                    }

                    yield break;
                }

                if (current?.ContentStarted == true)
                {
                    current.ContentLines.Add("===============================================");
                }
            }

            var time = TimeRegex.Match(line);
            if (current?.PendingSenderLine is not null && !time.Success)
            {
                current.ContentLines.Add(current.PendingSenderLine);
                current.PendingSenderLine = null;
                current.PendingSender = null;
                current.PendingSenderTitle = null;
            }

            if (IsFooterSeparator(line) && current?.ContentStarted == true)
            {
                pendingFooterSeparator = true;
                continue;
            }

            var number = NumberRegex.Match(line);
            if (number.Success)
            {
                if (current is not null)
                {
                    yield return BuildMessage(filePath, conversation, messageIndex++, current);
                    hasValidMessage = true;
                }

                current = new MessageBlock(lineNumber) { Number = number.Groups["number"].Value };
                continue;
            }

            if (time.Success)
            {
                if (current?.Number is not null && current.TimestampMs is not null)
                {
                    if (current.ContentStarted)
                    {
                        current.ContentLines.Add(line);
                    }

                    continue;
                }

                if (!ImportText.TryParseFlexibleTimestamp(time.Groups["value"].Value, out var timestampMs))
                {
                    throw new ImportFormatException(filePath, $"第 {messageIndex + 1} 个 QQ TXT 消息时间无效（第 {lineNumber} 行）");
                }

                if (current?.PendingSender is not null)
                {
                    var nextSender = current.PendingSender;
                    var nextSenderTitle = current.PendingSenderTitle;
                    current.PendingSender = null;
                    current.PendingSenderTitle = null;
                    current.PendingSenderLine = null;
                    yield return BuildMessage(filePath, conversation, messageIndex++, current);
                    hasValidMessage = true;
                    current = new MessageBlock(lineNumber)
                    {
                        Sender = nextSender,
                        SenderTitle = nextSenderTitle,
                    };
                }
                else if (current?.TimestampMs is not null)
                {
                    yield return BuildMessage(filePath, conversation, messageIndex++, current);
                    hasValidMessage = true;
                    current = new MessageBlock(lineNumber);
                }

                current ??= new MessageBlock(lineNumber);
                current.TimeText = time.Groups["value"].Value;
                current.TimestampMs = timestampMs;
                continue;
            }

            if (current is null)
            {
                if (TryReadSender(line, out var sender, out var senderTitle))
                {
                    current = new MessageBlock(lineNumber)
                    {
                        Sender = sender,
                        SenderTitle = senderTitle,
                    };
                }

                continue;
            }

            var type = TypeRegex.Match(line);
            if (type.Success && !current.ContentStarted)
            {
                current.TypeText = type.Groups["value"].Value;
                continue;
            }

            var content = ContentRegex.Match(line);
            if (content.Success && !current.ContentStarted)
            {
                current.ContentStarted = true;
                current.ContentLines.Add(content.Groups["value"].Value);
                continue;
            }

            var resourceCount = ResourceCountRegex.Match(line);
            if (resourceCount.Success && current.ContentStarted)
            {
                current.PendingResources = new ResourceCandidate(
                    int.Parse(resourceCount.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    line);
                continue;
            }

            if (!current.ContentStarted && TryReadSender(line, out var senderName, out var title))
            {
                current.Sender = senderName;
                current.SenderTitle = title;
                continue;
            }

            if (current.ContentStarted && current.Number is null && TryReadSender(line, out var pendingSender, out var pendingTitle))
            {
                current.PendingSender = pendingSender;
                current.PendingSenderTitle = pendingTitle;
                current.PendingSenderLine = line;
                continue;
            }

            if (current.ContentStarted)
            {
                current.ContentLines.Add(line);
            }
        }

        if (pendingFooterSeparator && current?.ContentStarted == true)
        {
            current.ContentLines.Add("===============================================");
        }

        if (current?.PendingResources is { } finalResources)
        {
            if (finalResources.Resources.Count == finalResources.DeclaredCount)
            {
                CommitResourceCandidate(current, finalResources);
            }
            else
            {
                ReplayResourceCandidate(current, finalResources);
            }
        }

        if (current?.PendingSenderLine is not null)
        {
            current.ContentLines.Add(current.PendingSenderLine);
            current.PendingSenderLine = null;
        }

        if (current is not null)
        {
            yield return BuildMessage(filePath, conversation, messageIndex, current);
            hasValidMessage = true;
        }

        if (!hasValidMessage)
        {
            throw new ImportFormatException(filePath, "未找到有效的 QQ TXT 消息块");
        }
    }

    private static Header ReadHeader(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        cancellationToken.ThrowIfCancellationRequested();
        var signature = reader.ReadLine();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(signature, Signature, StringComparison.Ordinal))
        {
            throw new ImportFormatException(filePath, "不是 QQ Chat Exporter V5 TXT 导出");
        }

        string? title = null;
        string? type = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = reader.ReadLine();
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null)
            {
                break;
            }

            var titleMatch = TitleRegex.Match(line);
            var typeMatch = ChatTypeRegex.Match(line);
            if (titleMatch.Success)
            {
                title = titleMatch.Groups["value"].Value.Trim();
            }
            else if (typeMatch.Success)
            {
                type = typeMatch.Groups["value"].Value.Trim();
            }

            if (NumberRegex.IsMatch(line) || TimeRegex.IsMatch(line))
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(type))
        {
            throw new ImportFormatException(filePath, "缺少当前 QQ TXT 文件头的聊天名称或聊天类型");
        }

        var kind = type switch
        {
            "群聊" => "group",
            "私聊" => "private",
            _ => throw new ImportFormatException(filePath, $"不支持的 QQ TXT 聊天类型：{type}"),
        };
        return new Header(title, kind);
    }

    private static void CommitResourceCandidate(MessageBlock block, ResourceCandidate candidate)
    {
        block.ResourceCount = candidate.DeclaredCount;
        block.Resources.AddRange(candidate.Resources);
        block.PendingResources = null;
    }

    private static void ReplayResourceCandidate(MessageBlock block, ResourceCandidate candidate)
    {
        block.ContentLines.AddRange(candidate.Lines);
        block.PendingResources = null;
    }

    private static ParsedMessage BuildMessage(
        string filePath,
        ParsedConversation conversation,
        int messageIndex,
        MessageBlock block)
    {
        if (block.TimestampMs is null || block.TimeText is null)
        {
            throw new ImportFormatException(filePath, $"第 {messageIndex + 1} 个 QQ TXT 消息缺少时间（始于第 {block.StartLine} 行）");
        }

        if (!block.ContentStarted)
        {
            throw new ImportFormatException(filePath, $"第 {messageIndex + 1} 个 QQ TXT 消息缺少内容（始于第 {block.StartLine} 行）");
        }

        TrimTrailingBlankLines(block.ContentLines);
        var content = string.Join("\n", block.ContentLines);
        var sender = block.Sender ?? "unknown";
        var type = block.TypeText is null ? "text" : MapType(block.TypeText);
        var isSystem = type == "system";
        var resources = new JsonArray();
        var attachments = new List<ParsedAttachment>();
        for (var ordinal = 0; ordinal < block.Resources.Count; ordinal++)
        {
            var resource = block.Resources[ordinal];
            resources.Add(new JsonObject
            {
                ["type"] = resource.Type,
                ["name"] = resource.Name,
            });
            attachments.Add(new ParsedAttachment(
                ordinal,
                MapResourceKind(resource.Type),
                resource.Name.Length == 0 ? null : resource.Name,
                null,
                null,
                null,
                ImportText.GuessMime(null, resource.Name),
                null,
                null,
                null,
                new JsonObject { ["resourceType"] = resource.Type, ["resourceName"] = resource.Name }));
        }

        var raw = new JsonObject
        {
            ["number"] = block.Number,
            ["sender"] = sender,
            ["senderTitle"] = block.SenderTitle,
            ["time"] = block.TimeText,
            ["type"] = block.TypeText,
            ["content"] = content,
            ["resourceCount"] = block.ResourceCount,
            ["resources"] = resources,
        };
        return FlatMessageFactory.Create(new FlatMessageData(
            null,
            block.Number,
            block.TimestampMs.Value,
            FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, sender),
            sender,
            isSystem ? "system" : "incoming",
            type,
            content,
            $"message:{messageIndex + 1}:line:{block.StartLine}",
            raw,
            attachments,
            block.Number,
            IsSystem: isSystem,
            MediaType: attachments.FirstOrDefault()?.Kind));
    }

    private static bool TryReadSender(string line, out string sender, out string? senderTitle)
    {
        sender = string.Empty;
        senderTitle = null;
        var match = SenderRegex.Match(line);
        if (!match.Success || line.StartsWith("时间:", StringComparison.Ordinal) || line.StartsWith("类型:", StringComparison.Ordinal)
            || line.StartsWith("内容:", StringComparison.Ordinal) || line.StartsWith("资源:", StringComparison.Ordinal))
        {
            return false;
        }

        var display = match.Groups["value"].Value.Trim();
        var titled = SenderTitleRegex.Match(display);
        if (titled.Success)
        {
            senderTitle = titled.Groups["title"].Value;
            display = titled.Groups["sender"].Value.Trim();
        }

        if (display.Length == 0)
        {
            return false;
        }

        sender = display;
        return true;
    }

    private static bool IsFooterSeparator(string line) => line == "===============================================";

    private static string MapType(string value) => value.Trim() switch
    {
        "text" or "文本" => "text",
        "image" or "图片" => "image",
        "video" or "视频" => "video",
        "audio" or "音频" => "audio",
        "file" or "文件" => "file",
        "face" or "表情" => "face",
        "reply" or "回复" => "reply",
        "system" or "系统消息" => "system",
        _ => "unknown",
    };

    private static string MapResourceKind(string value) => MapType(value) switch
    {
        "image" or "video" or "audio" or "file" => MapType(value),
        _ => "file",
    };

    private static void TrimTrailingBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }
    }

    private sealed record Header(string Title, string Kind);

    private sealed class MessageBlock(int startLine)
    {
        public int StartLine { get; } = startLine;
        public string? Number { get; init; }
        public string? Sender { get; set; }
        public string? SenderTitle { get; set; }
        public string? TimeText { get; set; }
        public long? TimestampMs { get; set; }
        public string? TypeText { get; set; }
        public bool ContentStarted { get; set; }
        public List<string> ContentLines { get; } = [];
        public string? PendingSender { get; set; }
        public string? PendingSenderTitle { get; set; }
        public string? PendingSenderLine { get; set; }
        public int? ResourceCount { get; set; }
        public List<ResourceLine> Resources { get; } = [];
        public ResourceCandidate? PendingResources { get; set; }
    }

    private sealed record ResourceLine(string Type, string Name);

    private sealed class ResourceCandidate(int declaredCount, string headerLine)
    {
        public int DeclaredCount { get; } = declaredCount;
        public List<string> Lines { get; } = [headerLine];
        public List<ResourceLine> Resources { get; } = [];
    }
}
