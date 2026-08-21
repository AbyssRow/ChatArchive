using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ChatArchive.Core.Importing;

/// <summary>WeFlow JSON 解析器，行为对齐旧版 wechat.py（解析器版本 4）。</summary>
public static class WeFlowParser
{
    private static readonly Dictionary<string, string> JsonMediaTypes = new()
    {
        ["图片消息"] = "image",
        ["文件消息"] = "file",
        ["视频消息"] = "video",
        ["语音消息"] = "voice",
        ["动画表情"] = "emoji",
    };

    private static readonly Dictionary<string, string> JsonMessageTypes = new()
    {
        ["文本消息"] = "text",
        ["图片消息"] = "image",
        ["文件消息"] = "file",
        ["视频消息"] = "video",
        ["语音消息"] = "audio",
        ["动画表情"] = "emoji",
        ["引用消息"] = "reply",
        ["系统消息"] = "system",
        ["小程序消息"] = "mini_program",
        ["聊天记录"] = "forward",
        ["转账消息"] = "transfer",
        ["链接消息"] = "link",
        ["通话消息"] = "call",
        ["位置消息"] = "location",
        ["名片消息"] = "contact",
        ["群公告"] = "system",
        ["其他消息"] = "other",
    };

    private static readonly IReadOnlyList<(string Prefix, string Type)> BracketLabels;
    private static readonly IReadOnlyList<string> SearchableFields = new[]
    {
        "quotedContent", "linkTitle", "linkUrl", "appMsgDesc",
        "appMsgSourceName", "locationLabel", "locationPoiname",
    };

    static WeFlowParser()
    {
        BracketLabels = new[]
        {
            ("[图片]", "image"),
            ("[文件]", "file"),
            ("[视频]", "video"),
            ("[语音]", "audio"),
            ("[表情包]", "emoji"),
            ("[转账]", "transfer"),
        };
    }

    public static (ParsedConversation Conversation, string? SelfSender) ReadConversation(JsonDocument document, string filePath)
    {
        JsonObject session;
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("session", out var sessionElement)
            || sessionElement.ValueKind != JsonValueKind.Object
            || JsonSerializer.Deserialize<JsonObject>(sessionElement.GetRawText()) is not { } parsedSession)
        {
            throw new ImportFormatException(filePath, "WeFlow session 无效");
        }

        session = parsedSession;
        var nativeId = ImportText.Clean(Get(session, "wxid"));
        if (nativeId.Length == 0)
        {
            throw new ImportFormatException(filePath, "缺少 WeFlow 会话 ID");
        }

        var sessionType = ImportText.Clean(Get(session, "type"));
        var kind = sessionType.Contains('群') || nativeId.EndsWith("@chatroom", StringComparison.Ordinal)
            ? "group"
            : "private";
        var title = FirstNonEmpty(
            ImportText.Clean(Get(session, "remark")),
            ImportText.Clean(Get(session, "displayName")),
            ImportText.Clean(Get(session, "nickname")));
        if (title.Length == 0)
        {
            title = TitleFromPath(filePath, nativeId);
        }

        // 统计自己发送消息的 senderUsername，取出现最多的作为本账号标识。
        var counter = new List<string>();
        var counts = new Dictionary<string, long>();
        foreach (var rawElement in MessagesOf(document))
        {
            if (!AsBool(rawElement.TryGetProperty("isSend", out var isSendElement) ? JsonNode.Parse(isSendElement.GetRawText()) : null))
            {
                continue;
            }

            var sender = ImportText.Clean(ElementString(rawElement, "senderUsername"));
            if (sender.Length == 0)
            {
                continue;
            }

            if (!counts.TryAdd(sender, 1))
            {
                counts[sender]++;
            }
            else
            {
                counter.Add(sender);
            }
        }

        if (kind == "private" && counts.Count > 1)
        {
            counts.Remove(nativeId);
            counter.RemoveAll(id => id == nativeId);
        }

        string? selfSender = null;
        long bestCount = 0;
        foreach (var candidate in counter)
        {
            if (counts[candidate] > bestCount)
            {
                bestCount = counts[candidate];
                selfSender = candidate;
            }
        }

        return (new ParsedConversation("wechat", "wechat-default", nativeId, kind, title), selfSender);
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        JsonDocument document, ParsedConversation conversation, string? selfSender, string filePath)
    {
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var index = 0;
        foreach (var rawElement in MessagesOf(document))
        {
            yield return ParseMessage(rawElement, index++, conversation, selfSender, exportRoot);
        }
    }

    private static ParsedMessage ParseMessage(
        JsonElement rawElement, int index, ParsedConversation conversation, string? selfSender, string exportRoot)
    {
        var raw = ElementToObject(rawElement);
        var rawContent = OrEmpty(ImportText.Clean(TryGetRaw(raw, "content")), "[空消息]");
        var isSend = AsBool(raw["isSend"]);
        var exportedSender = ImportText.Clean(TryGetRaw(raw, "senderUsername"));
        var senderNative = isSend && !string.IsNullOrEmpty(selfSender)
            ? selfSender!
            : exportedSender.Length > 0 ? exportedSender : "unknown";
        var exportedName = ImportText.Clean(TryGetRaw(raw, "senderDisplayName"));
        var senderName = isSend
            ? "我"
            : FirstNonEmpty(
                exportedName,
                conversation.Kind == "private" ? conversation.Title : "",
                senderNative);

        var localTypeNode = Get(raw, "localType");
        var exportedType = ImportText.Clean(TryGetRaw(raw, "type"));
        var mediaType = JsonMediaTypes.TryGetValue(exportedType, out var mappedMedia) ? mappedMedia : null;
        var legacyMessageType = MessageType(localTypeNode, mediaType, rawContent);
        var previousMessageType = JsonMessageTypes.TryGetValue(exportedType, out var labeled)
            ? labeled
            : legacyMessageType;

        var (content, xmlMessageType, xmlSearchValues) = NormalizeContent(rawContent, exportedSender);
        var messageType = xmlMessageType ?? previousMessageType;

        var mediaReference = MediaReference(exportRoot, exportedType, mediaType, content);
        var previousMediaReference = MediaReference(exportRoot, exportedType, mediaType, rawContent);

        var attachments = new List<ParsedAttachment>();
        string? sourcePath = null;
        if (mediaReference is { } reference)
        {
            sourcePath = reference.SourcePath;
            attachments.Add(new ParsedAttachment(
                Ordinal: 0,
                Kind: messageType != "other" ? messageType : "file",
                Filename: reference.Filename,
                DeclaredPath: reference.DeclaredPath,
                SourcePath: reference.SourcePath,
                DeclaredSize: null,
                MimeType: ImportText.GuessMime(reference.SourcePath, reference.Filename),
                Width: null,
                Height: null,
                Duration: null,
                Metadata: new JsonObject()));
        }

        var timestamp = ImportText.AsLong(raw["createTime"]) ?? 0;
        var timestampMs = timestamp >= 10_000_000_000 ? timestamp : timestamp * 1000;
        var localId = OrNull(ImportText.Clean(TryGetRaw(raw, "localId")));
        var nativeId = OrNull(ImportText.Clean(TryGetRaw(raw, "platformMessageId")));
        var replyToNativeId = OrNull(FirstNonEmpty(
            ImportText.Clean(TryGetRaw(raw, "replyToMessageId")),
            ImportText.Clean(TryGetRaw(raw, "quotedSvrid"))));
        var isSystem = messageType == "system";
        var isRecalled = isSystem && content.Contains("撤回", StringComparison.Ordinal);
        var direction = isSystem ? "system" : isSend ? "outgoing" : "incoming";
        var normalizedSearchText = BuildSearchText(raw, content, xmlSearchValues);

        var mediaFileName = sourcePath is null ? null : Path.GetFileName(sourcePath);
        var previousSourcePath = previousMediaReference?.SourcePath;
        var previousMediaName = previousSourcePath is null ? null : Path.GetFileName(previousSourcePath);
        var previousDirection = previousMessageType == "system"
            ? "system"
            : isSend ? "outgoing" : "incoming";
        var legacySender = exportedSender.Length > 0
            ? exportedSender
            : isSend && !string.IsNullOrEmpty(selfSender) ? selfSender! : "unknown";
        var legacyDirection = legacyMessageType == "system"
            ? "system"
            : isSend ? "outgoing" : "incoming";

        var semantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
            ["local_type"] = LocalTypeString(localTypeNode),
            ["media_type"] = NullStr(mediaType),
            ["message_type"] = messageType,
            ["content"] = content,
            ["media_name"] = NullStr(mediaFileName),
            ["reply_to_native_id"] = NullStr(replyToNativeId),
            ["search_text"] = normalizedSearchText,
        };

        var previousSemantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = previousDirection,
            ["local_type"] = LocalTypeString(localTypeNode),
            ["media_type"] = NullStr(mediaType),
            ["message_type"] = previousMessageType,
            ["content"] = rawContent,
            ["media_name"] = NullStr(previousMediaName),
            ["reply_to_native_id"] = NullStr(replyToNativeId),
            ["search_text"] = BuildSearchText(raw, rawContent),
        };

        var legacySemantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = legacySender,
            ["direction"] = legacyDirection,
            ["local_type"] = LocalTypeString(localTypeNode),
            ["media_type"] = NullStr(mediaType),
            ["content"] = rawContent,
            ["media_name"] = NullStr(previousSourcePath is not null && File.Exists(previousSourcePath) ? previousMediaName : null),
        };

        var payloadHash = CanonicalJson.HashHex(semantic);
        var legacyHashes = new HashSet<string>
        {
            CanonicalJson.HashHex(legacySemantic),
            CanonicalJson.HashHex(previousSemantic),
        };
        if (mediaFileName is not null)
        {
            var clone = (JsonObject)semantic.DeepClone();
            clone["media_name"] = null;
            legacyHashes.Add(CanonicalJson.HashHex(clone));
        }

        if (legacySemantic["media_name"] is not null)
        {
            var clone = (JsonObject)legacySemantic.DeepClone();
            clone["media_name"] = null;
            legacyHashes.Add(CanonicalJson.HashHex(clone));
        }

        return new ParsedMessage(
            NativeId: nativeId,
            LocalId: localId,
            TimestampMs: timestampMs,
            Sequence: null,
            SenderNativeId: senderNative,
            SenderName: senderName,
            SenderAliases: isSend
                ? new[] { senderName, senderNative }
                : new[] { senderName, exportedName, senderNative },
            Direction: direction,
            MessageType: messageType,
            MediaType: mediaType ?? (attachments.Count > 0 ? messageType : null),
            Content: content,
            SearchText: normalizedSearchText,
            IsRecalled: isRecalled,
            IsSystem: isSystem,
            ReplyToNativeId: replyToNativeId,
            PayloadHash: payloadHash,
            SemanticHash: CanonicalJson.HashHex(new JsonObject
            {
                ["timestamp_ms"] = timestampMs,
                ["sender"] = senderNative,
                ["direction"] = direction,
                ["local_type"] = LocalTypeString(localTypeNode),
            }),
            SourceLocator: $"message:{index}:local:{localId ?? string.Empty}",
            RawPayload: raw,
            Attachments: attachments,
            CompatiblePayloadHashes: legacyHashes
                .Where(h => h != payloadHash)
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToList());
    }

    internal static string MessageType(JsonNode? localTypeNode, string? mediaType, string content)
    {
        var media = ImportText.Clean(mediaType).ToLowerInvariant();
        if (media.Length > 0)
        {
            return media == "voice" ? "audio" : media;
        }

        var code = ImportText.AsLong(localTypeNode) ?? 0;
        switch (code)
        {
            case 1: return "text";
            case 3: return "image";
            case 34: return "audio";
            case 43: return "video";
            case 47: return "emoji";
            case 10000: return "system";
        }

        foreach (var (prefix, type) in BracketLabels)
        {
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                return type;
            }
        }

        return "other";
    }

    internal static (string Summary, string Type, IReadOnlyList<string> ExtraSearch)? SummarizeXml(string content)
    {
        var xml = content.TrimStart();
        if (!xml.StartsWith("<?xml", StringComparison.Ordinal) && !xml.StartsWith("<msg", StringComparison.Ordinal))
        {
            return null;
        }

        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return ("[微信 XML 消息]", "other", Array.Empty<string>());
        }

        var payment = root.Descendants("wcpayinfo").FirstOrDefault();
        if (payment is not null)
        {
            var scene = XmlText(payment, "scenetext");
            if (scene.Length == 0)
            {
                scene = "微信收款";
            }

            var title = XmlText(payment, "receivertitle");
            if (title.Length == 0)
            {
                title = XmlText(payment, "sendertitle");
            }

            var detail = XmlText(payment, "receiverdes");
            if (detail.Length == 0)
            {
                detail = XmlText(payment, "senderdes");
            }

            var summary = $"[{scene}]";
            if (title.Length > 0)
            {
                summary += $" {title}";
            }

            if (detail.Length > 0)
            {
                summary += $" - {detail}";
            }

            return (summary, "transfer", new[] { scene, title, detail });
        }

        var contactUsername = Attr(root, "username");
        var contactName = Attr(root, "nickname");
        var contactCompany = Attr(root, "openimdesc");
        if (contactUsername.Length > 0 || contactName.Length > 0 || contactCompany.Length > 0)
        {
            var label = contactCompany.Length > 0 || contactUsername.EndsWith("@openim", StringComparison.Ordinal)
                ? "企业微信名片"
                : "微信名片";
            var identity = FirstNonEmpty(contactName, contactUsername, "未命名联系人");
            if (contactCompany.Length > 0)
            {
                identity += $"（{contactCompany}）";
            }

            return ($"[{label}] {identity}", "contact", new[] { contactName, contactCompany, contactUsername });
        }

        var finder = root.Descendants("finderFeed").FirstOrDefault();
        if (finder is not null)
        {
            var nickname = XmlText(finder, "nickname");
            var description = XmlText(finder, "desc");
            var summary = "[视频号]";
            if (nickname.Length > 0)
            {
                summary += $" {nickname}";
            }

            if (description.Length > 0)
            {
                summary += $"\n{description}";
            }

            return (summary, "video", new[] { nickname, description });
        }

        var appMessage = root.Descendants("appmsg").FirstOrDefault();
        if (appMessage is not null)
        {
            var appType = XmlText(appMessage, "type");
            if (appType == "8")
            {
                return ("[动画表情]", "emoji", Array.Empty<string>());
            }

            var title = XmlText(appMessage, "title");
            var description = XmlText(appMessage, "des");
            if (title.Length > 0 || description.Length > 0)
            {
                var summary = $"[微信分享] {FirstNonEmpty(title, description)}";
                if (title.Length > 0 && description.Length > 0 && description != title)
                {
                    summary += $" - {description}";
                }

                return (summary, "link", new[] { title, description });
            }
        }

        return ("[微信 XML 消息]", "other", Array.Empty<string>());
    }

    internal static (string Content, string? Type, IReadOnlyList<string> Extras) NormalizeContent(
        string rawContent, string exportedSender)
    {
        var content = rawContent;
        if (exportedSender.EndsWith("@openim", StringComparison.Ordinal))
        {
            var prefix = $"{exportedSender}:";
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                content = ImportText.Clean(content[prefix.Length..]);
                if (content.Length == 0)
                {
                    content = "[空消息]";
                }
            }
        }

        var summarized = SummarizeXml(content);
        if (summarized is { } result)
        {
            return result;
        }

        return (content, null, Array.Empty<string>());
    }

    internal sealed record MediaRef(string? Filename, string? DeclaredPath, string? SourcePath);

    internal static MediaRef? MediaReference(string exportRoot, string exportedType, string? mediaType, string content)
    {
        if (exportedType == "文件消息" && content.StartsWith("[文件]", StringComparison.Ordinal))
        {
            var filename = System.Net.WebUtility.HtmlDecode(content["[文件]".Length..]).Trim();
            if (filename.Length == 0)
            {
                return new MediaRef(null, content, null);
            }

            if (filename is "." or ".." || filename.Contains('/') || filename.Contains('\\') || filename.Contains(':'))
            {
                return new MediaRef(null, content, null);
            }

            var extension = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0 || !extension.All(char.IsAsciiLetterOrDigit))
            {
                return new MediaRef(filename, content, null);
            }

            var declaredPath = $"media/files/{extension}/{filename}";
            return new MediaRef(filename, declaredPath, ImportText.SafeExportPath(exportRoot, declaredPath));
        }

        if (!string.IsNullOrEmpty(mediaType)
            && (content.Contains('/') || content.Contains('\\'))
            && !content.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !content.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = content.Replace('\\', '/');
            var filename = Path.GetFileName(normalized);
            if (filename.Length == 0)
            {
                filename = null;
            }

            return new MediaRef(filename, content, ImportText.SafeExportPath(exportRoot, content));
        }

        return null;
    }

    internal static string BuildSearchText(JsonObject raw, string content, IReadOnlyList<string>? extraValues = null)
    {
        var values = new List<string> { content };
        if (extraValues is not null)
        {
            foreach (var extra in extraValues)
            {
                AddUnique(values, ImportText.Clean(extra));
            }
        }

        foreach (var field in SearchableFields)
        {
            AddUnique(values, TryGetRaw(raw, field));
        }

        return string.Join("\n", values);
    }

    internal static string TitleFromPath(string filePath, string nativeId)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(filePath)));
        if (string.IsNullOrEmpty(name))
        {
            name = Path.GetFileNameWithoutExtension(filePath);
        }

        name = Regex.Replace(name, @"^(私聊|群聊)[_-]", string.Empty);
        name = Regex.Replace(name, @"_(全部时间|\d{4}[^_]*)$", string.Empty);
        name = ImportText.Clean(name);
        return name.Length > 0 ? name : nativeId;
    }

    internal static bool AsBool(JsonNode? value)
    {
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                var trimmed = text.Trim().ToLowerInvariant();
                return trimmed is "1" or "true" or "yes";
            }

            if (scalar.TryGetValue<bool>(out var b))
            {
                return b;
            }

            if (scalar.TryGetValue<long>(out var l))
            {
                return l != 0;
            }
        }

        return false;
    }

    private static void AddUnique(List<string> values, string candidate)
    {
        if (candidate.Length > 0 && !values.Contains(candidate))
        {
            values.Add(candidate);
        }
    }

    private static string XmlText(XElement parent, string name)
    {
        return ImportText.Clean(parent.Element(name)?.Value);
    }

    private static string Attr(XElement element, string name)
    {
        return ImportText.Clean(element.Attribute(name)?.Value);
    }

    private static IEnumerable<JsonElement> MessagesOf(JsonDocument document)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in messages.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static JsonObject ElementToObject(JsonElement element)
    {
        return JsonSerializer.Deserialize<JsonObject>(element.GetRawText())
            ?? throw new InvalidOperationException("消息不是 JSON 对象");
    }

    private static string TryGetRaw(JsonObject obj, string key)
    {
        return ImportText.RawText(obj.TryGetPropertyValue(key, out var value) ? value : null);
    }

    private static JsonNode? Get(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var value) ? value : null;
    }

    private static string LocalTypeString(JsonNode? node)
    {
        if (node is null)
        {
            return "None";
        }

        var raw = node.ToJsonString();
        return raw is "true" or "false" ? raw == "true" ? "True" : "False" : raw.Trim('"');
    }

    private static JsonNode? NullStr(string? value)
    {
        return value is null ? null : JsonValue.Create(value);
    }

    private static string ElementString(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out var property))
        {
            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : property.ToString();
        }

        return string.Empty;
    }

    private static string OrEmpty(string value, string fallback)
    {
        return value.Length > 0 ? value : fallback;
    }

    private static string? OrNull(string value)
    {
        return value.Length > 0 ? value : null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }
}


