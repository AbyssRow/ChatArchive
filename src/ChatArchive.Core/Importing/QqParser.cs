using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

/// <summary>QQ Chat Exporter JSON 解析器，行为对齐旧版 qq.py（解析器版本 5）。</summary>
public static class QqParser
{
    private static readonly HashSet<string> SearchableKeys = new()
    {
        "text", "content", "summary", "title", "filename", "senderName",
    };

    public static ParsedConversation ReadConversation(JsonDocument document, string filePath)
    {
        var chat = GetTopObject(document, "chatInfo")
            ?? throw new ImportFormatException(filePath, "缺少 chatInfo");

        return ReadConversation(chat, filePath);
    }

    internal static ParsedConversation ReadConversation(JsonObject chat, string filePath)
    {
        var accountId = Pick(chat, "selfUin", "selfUid", fallback: "qq-default");
        var nativeId = Pick(chat, "peerUid", "peerUin", "name", fallback: Path.GetFileNameWithoutExtension(filePath));
        var title = Pick(chat, "name", "peerName", fallback: nativeId);
        var kind = string.Equals(ImportText.Clean(GetNode(chat, "type")), "group", StringComparison.OrdinalIgnoreCase)
            ? "group"
            : "private";
        return new ParsedConversation("qq", accountId, nativeId, kind, title);
    }

    public static IEnumerable<ParsedMessage> IterateMessages(JsonDocument document, ParsedConversation conversation, string documentPath)
    {
        var chat = GetTopObject(document, "chatInfo")!;
        var selfUid = ImportText.Clean(GetNode(chat, "selfUid"));
        var selfUin = ImportText.Clean(GetNode(chat, "selfUin"));

        if (document.RootElement.TryGetProperty("messages", out var messagesElement)
            && messagesElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in messagesElement.EnumerateArray())
            {
                yield return ParseMessage(
                    JsonObjectFrom(item),
                    index,
                    selfUid,
                    selfUin,
                    Path.GetDirectoryName(Path.GetFullPath(documentPath))!);
                index++;
            }
        }
    }

    internal static IEnumerable<ParsedMessage> IterateMessages(
        IEnumerable<JsonObject> messages,
        ParsedConversation conversation,
        string documentPath,
        string? selfUid = null,
        string? selfUin = null)
    {
        var resolvedSelfUid = selfUid ?? conversation.AccountId;
        var resolvedSelfUin = selfUin ?? conversation.AccountId;
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        var index = 0;
        foreach (var raw in messages)
        {
            yield return ParseMessage(raw, index++, resolvedSelfUid, resolvedSelfUin, exportRoot);
        }
    }

    private static ParsedMessage ParseMessage(JsonObject raw, int index, string selfUid, string selfUin, string exportRoot)
    {
        var sender = raw["sender"] as JsonObject;
        var senderNative = FirstNonEmpty(
            SenderField(sender, "uid"),
            SenderField(sender, "uin"),
            "unknown");

        var aliases = new List<string>();
        foreach (var key in new[] { "groupCard", "name", "nickname", "remark", "uin" })
        {
            var alias = ImportText.Clean(SenderField(sender, key));
            if (alias.Length > 0)
            {
                aliases.Add(alias);
            }
        }

        aliases = aliases.Distinct().ToList();
        var senderName = aliases.Count > 0 ? aliases[0] : senderNative;
        var uinRaw = ImportText.Clean(SenderField(sender, "uin"));
        var isSelf = senderNative == selfUid || senderNative == selfUin || uinRaw == selfUin;

        var content = raw["content"] as JsonObject ?? new JsonObject();
        var displayContent = ImportText.Clean(GetNode(content, "text"));
        var searchable = new List<string>();
        CollectSearchable(content, searchable, key: null);
        searchable = searchable
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
        if (displayContent.Length == 0)
        {
            var typeLabel = ImportText.Clean(GetNode(raw, "type"));
            var fallbackType = typeLabel.Length > 0 ? typeLabel : "message";
            displayContent = searchable.Count > 0 ? searchable[0] : $"[{fallbackType}]";
        }

        var searchText = searchable.Count > 0 ? string.Join("\n", searchable) : displayContent;
        var timestampMs = ImportText.AsLong(raw["timestamp"]) ?? 0;
        var messageType = ImportText.Clean(GetNode(raw, "type")).ToLowerInvariant();
        if (messageType.Length == 0)
        {
            messageType = "unknown";
        }

        var attachments = BuildAttachments(content, exportRoot);
        var replyTo = ReplyId(content);
        var recalled = IsTruthy(raw["recalled"]);
        var system = IsTruthy(raw["system"]);
        var direction = system ? "system" : isSelf ? "outgoing" : "incoming";

        var logicalAttachments = new JsonArray();
        foreach (var attachment in attachments)
        {
            logicalAttachments.Add(new JsonObject
            {
                ["kind"] = Str(attachment.Kind),
                ["filename"] = NullStr(attachment.Filename),
                ["size"] = NullLong(attachment.DeclaredSize),
                ["width"] = NullLong(attachment.Width),
                ["height"] = NullLong(attachment.Height),
                ["duration"] = NullDouble(attachment.Duration),
                ["md5"] = NullStr(ImportText.Clean(attachment.Metadata["md5"])),
            });
        }

        var sequence = OrNull(ImportText.Clean(GetNode(raw, "seq")));
        var semantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
            ["message_type"] = messageType,
            ["content"] = displayContent,
            ["attachments"] = logicalAttachments.DeepClone(),
            ["recalled"] = recalled,
            ["system"] = system,
            ["reply_to"] = NullStr(replyTo),
            ["search_text"] = searchText,
            ["sequence"] = NullStr(sequence),
        };
        var legacySemantic = (JsonObject)semantic.DeepClone();
        legacySemantic.Remove("search_text");
        legacySemantic.Remove("sequence");

        var payloadHash = CanonicalJson.HashHex(semantic);
        var legacyPayloadHash = CanonicalJson.HashHex(legacySemantic);

        return new ParsedMessage(
            NativeId: OrNull(ImportText.Clean(GetNode(raw, "id"))),
            LocalId: null,
            TimestampMs: timestampMs,
            Sequence: sequence,
            SenderNativeId: senderNative,
            SenderName: senderName,
            SenderAliases: aliases,
            Direction: direction,
            MessageType: messageType,
            MediaType: attachments.Count > 0 ? attachments[0].Kind : null,
            Content: displayContent,
            SearchText: searchText,
            IsRecalled: recalled,
            IsSystem: system,
            ReplyToNativeId: replyTo,
            PayloadHash: payloadHash,
            SemanticHash: CanonicalJson.HashHex(new JsonObject
            {
                ["timestamp_ms"] = timestampMs,
                ["sender"] = senderNative,
                ["message_type"] = messageType,
                ["content"] = displayContent,
            }),
            SourceLocator: $"message:{index}",
            RawPayload: raw,
            Attachments: attachments,
            CompatiblePayloadHashes:
                legacyPayloadHash != payloadHash ? new[] { legacyPayloadHash } : Array.Empty<string>());
    }

    internal static void CollectSearchable(JsonNode? value, List<string> found, string? key)
    {
        switch (value)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    CollectSearchable(property.Value, found, property.Key);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    CollectSearchable(child, found, key);
                }

                break;
            default:
                if (key is null || !SearchableKeys.Contains(key) || value is not JsonValue scalar)
                {
                    return;
                }

                string text;
                if (scalar.TryGetValue<string>(out var s))
                {
                    text = ImportText.Clean(s);
                }
                else
                {
                    var rawScalar = scalar.ToJsonString();
                    text = rawScalar is "true" or "false" or "null"
                        ? rawScalar switch
                        {
                            "true" => "True",
                            "false" => "False",
                            _ => string.Empty,
                        }
                        : ImportText.Clean(rawScalar);
                }

                if (text.Length > 0
                    && !text.StartsWith("http://", StringComparison.Ordinal)
                    && !text.StartsWith("https://", StringComparison.Ordinal)
                    && !text.StartsWith("data:image/", StringComparison.Ordinal))
                {
                    found.Add(text);
                }

                break;
        }
    }

    internal static List<ParsedAttachment> BuildAttachments(JsonObject content, string exportRoot)
    {
        var resources = new List<JsonObject>();
        if (content["resources"] is JsonArray provided)
        {
            resources.AddRange(provided.OfType<JsonObject>().Select(r => r));
        }

        if (resources.Count == 0 && content["elements"] is JsonArray elements)
        {
            foreach (var element in elements.OfType<JsonObject>())
            {
                var kind = ImportText.Clean(element["type"]).ToLowerInvariant();
                if (kind is "image" or "video" or "audio" or "file" or "voice")
                {
                    var data = element["data"] as JsonObject ?? new JsonObject();
                    var clone = (JsonObject)data.DeepClone();
                    if (clone["type"] is null)
                    {
                        clone["type"] = kind;
                    }

                    resources.Add(clone);
                }
            }
        }

        var exportRootFixed = exportRoot;
        var result = new List<ParsedAttachment>();
        for (var ordinal = 0; ordinal < resources.Count; ordinal++)
        {
            var resource = resources[ordinal];
            var kind = ImportText.Clean(FirstNode(resource, "type", "mediaType")).ToLowerInvariant();
            if (kind == "voice")
            {
                kind = "audio";
            }

            if (kind.Length == 0)
            {
                kind = "file";
            }

            var filename = OrNull(FirstClean(resource, "filename", "name"));
            var (declaredPath, sourcePath) = ResolveSourcePath(exportRootFixed, resource);
            var mimeType = FirstClean(resource, "mimeType");
            var mime = mimeType.Length > 0 ? mimeType : ImportText.GuessMime(sourcePath, filename);

            var metadata = new JsonObject();
            foreach (var property in resource)
            {
                if (property.Key is not ("url" or "localPath" or "filename" or "name" or "type"))
                {
                    metadata[property.Key] = property.Value?.DeepClone();
                }
            }

            result.Add(new ParsedAttachment(
                Ordinal: ordinal,
                Kind: kind,
                Filename: filename,
                DeclaredPath: declaredPath,
                SourcePath: sourcePath,
                DeclaredSize: ImportText.AsLong(resource["size"]),
                MimeType: mime,
                Width: AsNullableInt(resource["width"]),
                Height: AsNullableInt(resource["height"]),
                Duration: ImportText.AsDouble(resource["duration"]),
                Metadata: metadata));
        }

        return result;
    }

    /// <summary>占位：QQ 导出根目录在服务层注入前先按文件所在目录处理。</summary>
    private static (string? Declared, string? Source) ResolveSourcePath(string exportRoot, JsonObject resource)
    {
        var url = ImportText.Clean(resource["url"]);
        var localPath = ImportText.Clean(resource["localPath"]);
        var candidates = new List<string>();
        if (url.Length > 0 && !url.StartsWith("http://", StringComparison.Ordinal) && !url.StartsWith("https://", StringComparison.Ordinal))
        {
            var candidate = ImportText.SafeExportPath(exportRoot, url);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        if (localPath.Length > 0)
        {
            var normalized = localPath.Replace('\\', '/');
            if (!Path.IsPathRooted(normalized))
            {
                foreach (var relative in new[] { "resources/" + normalized, normalized })
                {
                    var candidate = ImportText.SafeExportPath(exportRoot, relative);
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return (url.Length > 0 ? url : localPath.Length > 0 ? localPath : null, candidate);
            }
        }

        var declared = url.Length > 0 ? url : localPath.Length > 0 ? localPath : null;
        return (declared, candidates.Count > 0 ? candidates[0] : null);
    }

    internal static string? ReplyId(JsonObject content)
    {
        if (content["elements"] is not JsonArray elements)
        {
            return null;
        }

        foreach (var element in elements.OfType<JsonObject>())
        {
            if (!string.Equals(ImportText.Clean(element["type"]), "reply", StringComparison.Ordinal))
            {
                continue;
            }

            if (element["data"] is not JsonObject data)
            {
                continue;
            }

            var value = FirstNode(data, "referencedMessageId", "messageId");
            if (value is null)
            {
                continue;
            }

            var trimmed = value.ToJsonString().Trim('"');
            if (trimmed is not ("" or "0"))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static JsonObject JsonObjectFrom(JsonElement element)
    {
        return JsonSerializer.Deserialize<JsonObject>(element.GetRawText())
            ?? throw new InvalidOperationException("消息不是 JSON 对象");
    }

    internal static bool IsTruthy(JsonNode? node)
    {
        return node switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<long>(out var l) => l != 0,
            JsonValue v when v.TryGetValue<string>(out var s) => s is not ("" or "0" or "false"),
            _ => false,
        };
    }

    private static int? AsNullableInt(JsonNode? node)
    {
        var parsed = ImportText.AsLong(node);
        return parsed.HasValue ? (int?)checked((int)Math.Clamp(parsed.Value, int.MinValue, int.MaxValue)) : null;
    }

    private static JsonNode? GetNode(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var value) ? value : null;
    }

    private static JsonNode? FirstNode(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string FirstClean(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var cleaned = ImportText.Clean(GetNode(obj, key));
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        return string.Empty;
    }

    private static string SenderField(JsonObject? sender, string key)
    {
        if (sender is null)
        {
            return string.Empty;
        }

        return ImportText.RawText(GetNode(sender, key));
    }

    private static string Pick(JsonObject obj, string[] keys, string fallback)
    {
        var value = FirstClean(obj, keys);
        return value.Length > 0 ? value : fallback;
    }

    private static string Pick(JsonObject obj, string key1, string key2, string fallback)
    {
        return Pick(obj, new[] { key1, key2 }, fallback);
    }

    private static string Pick(JsonObject obj, string key1, string key2, string key3, string fallback)
    {
        return Pick(obj, new[] { key1, key2, key3 }, fallback);
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

        return values[^1];
    }

    private static string? OrNull(string value)
    {
        return value.Length > 0 ? value : null;
    }

    private static JsonNode? NullStr(string? value)
    {
        return value is null ? null : JsonValue.Create(value);
    }

    private static JsonNode Str(string value)
    {
        return JsonValue.Create(value)!;
    }

    private static JsonNode? NullLong(long? value)
    {
        return value.HasValue ? JsonValue.Create(value.Value) : null;
    }

    private static JsonNode? NullDouble(double? value)
    {
        return value.HasValue ? JsonValue.Create(value.Value) : null;
    }

    private static JsonObject? GetTopObject(JsonDocument document, string key)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!document.RootElement.TryGetProperty(key, out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonSerializer.Deserialize<JsonObject>(element.GetRawText());
    }
}

