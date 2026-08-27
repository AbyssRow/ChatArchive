using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

/// <summary>ChatLab 0.0.2 规范 (JSON &amp; JSONL) 解析器。</summary>
public static class ChatLabParser
{
    private static readonly Dictionary<int, string> IntMessageTypes = new()
    {
        [0] = "text",
        [1] = "image",
        [2] = "audio",
        [3] = "video",
        [4] = "file",
        [5] = "emoji",
        [7] = "link",
        [8] = "location",
        [23] = "call",
        [24] = "mini_program",
        [25] = "reply",
        [27] = "contact",
        [80] = "system",
        [99] = "other",
    };

    private static readonly Dictionary<string, string> JsonMessageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["文本消息"] = "text",
        ["text"] = "text",
        ["图片消息"] = "image",
        ["image"] = "image",
        ["文件消息"] = "file",
        ["file"] = "file",
        ["视频消息"] = "video",
        ["video"] = "video",
        ["语音消息"] = "audio",
        ["audio"] = "audio",
        ["voice"] = "audio",
        ["动画表情"] = "emoji",
        ["表情消息"] = "emoji",
        ["emoji"] = "emoji",
        ["引用消息"] = "reply",
        ["reply"] = "reply",
        ["系统消息"] = "system",
        ["system"] = "system",
        ["小程序消息"] = "mini_program",
        ["mini_program"] = "mini_program",
        ["聊天记录"] = "forward",
        ["forward"] = "forward",
        ["转账消息"] = "transfer",
        ["微信转账"] = "transfer",
        ["微信红包"] = "transfer",
        ["红包消息"] = "transfer",
        ["transfer"] = "transfer",
        ["链接消息"] = "link",
        ["link"] = "link",
        ["通话消息"] = "call",
        ["call"] = "call",
        ["位置消息"] = "location",
        ["location"] = "location",
        ["名片消息"] = "contact",
        ["contact"] = "contact",
        ["群公告"] = "system",
        ["其他消息"] = "other",
        ["other"] = "other",
    };

    private static readonly IReadOnlyList<(string Prefix, string Type)> BracketLabels = new[]
    {
        ("[图片]", "image"),
        ("[文件]", "file"),
        ("[视频]", "video"),
        ("[语音]", "audio"),
        ("[表情包]", "emoji"),
        ("[动画表情]", "emoji"),
        ("[转账]", "transfer"),
        ("[微信转账]", "transfer"),
        ("[微信红包]", "transfer"),
        ("[位置]", "location"),
        ("[名片]", "contact"),
        ("[链接]", "link"),
        ("[小程序]", "mini_program"),
        ("[通话]", "call"),
    };

    private static readonly IReadOnlyList<string> SearchableFields = new[]
    {
        "quotedContent", "linkTitle", "linkUrl", "appMsgDesc",
        "appMsgSourceName", "locationLabel", "locationPoiname",
        "musicTitle", "finderTitle", "title", "description", "summary", "address", "poiName",
    };

    public static ParsedConversation ReadConversation(
        JsonObject meta,
        string filePath,
        IReadOnlyList<JsonObject>? members = null)
    {
        var platform = ImportText.Clean(FirstNonEmpty(TryGetRaw(meta, "platform"), "wechat")).ToLowerInvariant();
        if (platform.Length == 0)
        {
            platform = "wechat";
        }

        var ownerId = ImportText.Clean(FirstNonEmpty(
            TryGetRaw(meta, "ownerId"),
            TryGetRaw(meta, "ownerID"),
            TryGetRaw(meta, "selfWxid"),
            TryGetRaw(meta, "selfId"),
            TryGetRaw(meta, "accountId"),
            TryGetRaw(meta, "selfUid"),
            TryGetRaw(meta, "selfUin")));
        var accountId = ownerId.Length > 0 ? ownerId : $"{platform}-default";

        var sessionType = ImportText.Clean(FirstNonEmpty(TryGetRaw(meta, "type"), TryGetRaw(meta, "kind")));
        var groupId = ImportText.Clean(FirstNonEmpty(TryGetRaw(meta, "groupId"), TryGetRaw(meta, "group_id")));
        var rawNativeId = ImportText.Clean(FirstNonEmpty(
            groupId,
            TryGetRaw(meta, "nativeId"),
            TryGetRaw(meta, "chatId"),
            TryGetRaw(meta, "sessionId"),
            TryGetRaw(meta, "wxid"),
            TryGetRaw(meta, "id"),
            TryGetRaw(meta, "peerId"),
            TryGetRaw(meta, "targetId"),
            TryGetRaw(meta, "peerUid"),
            TryGetRaw(meta, "peerUin")));

        var isGroup = sessionType.Equals("group", StringComparison.OrdinalIgnoreCase)
            || sessionType.Contains('群')
            || AsBool(meta["isGroup"])
            || rawNativeId.EndsWith("@chatroom", StringComparison.Ordinal)
            || groupId.Length > 0;
        var kind = isGroup ? "group" : "private";

        string nativeId;
        if (rawNativeId.Length > 0)
        {
            nativeId = rawNativeId;
        }
        else if (members != null && members.Count > 0)
        {
            string? peerFromMember = null;
            if (kind == "private")
            {
                foreach (var member in members)
                {
                    var mId = ExtractMemberPlatformId(member);
                    if (mId.Length > 0 && !string.Equals(mId, ownerId, StringComparison.OrdinalIgnoreCase))
                    {
                        peerFromMember = mId;
                        break;
                    }
                }
            }

            nativeId = peerFromMember
                ?? ExtractMemberPlatformId(members[0])
                ?? (ownerId.Length > 0 ? ownerId : TitleFromPath(filePath, "chat"));
        }
        else
        {
            nativeId = ownerId.Length > 0 ? ownerId : TitleFromPath(filePath, "chat");
        }

        var title = FirstNonEmpty(
            ImportText.Clean(TryGetRaw(meta, "name")),
            ImportText.Clean(TryGetRaw(meta, "title")),
            ImportText.Clean(TryGetRaw(meta, "displayName")),
            ImportText.Clean(TryGetRaw(meta, "remark")),
            ImportText.Clean(TryGetRaw(meta, "groupName")));
        if (title.Length == 0)
        {
            title = TitleFromPath(filePath, nativeId);
        }

        return new ParsedConversation(platform, accountId, nativeId, kind, title);
    }

    internal static string ExtractMemberPlatformId(JsonObject member)
    {
        return ImportText.Clean(FirstNonEmpty(
            TryGetRaw(member, "platformId"),
            TryGetRaw(member, "platform_id"),
            TryGetRaw(member, "senderId"),
            TryGetRaw(member, "wxid"),
            TryGetRaw(member, "id"),
            TryGetRaw(member, "uid"),
            TryGetRaw(member, "userId")));
    }

    public static Dictionary<string, JsonObject> BuildMemberDictionary(IEnumerable<JsonObject>? members)
    {
        var dict = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (members != null)
        {
            foreach (var m in members)
            {
                var id = ExtractMemberPlatformId(m);
                if (id.Length > 0)
                {
                    dict[id] = m;
                }
            }
        }

        return dict;
    }

    internal static string? InferSelfSender(
        IEnumerable<JsonObject> messages,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        var counter = new List<string>();
        var counts = new Dictionary<string, long>();
        foreach (var raw in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isSend = AsBool(raw["isSend"])
                || AsBool(raw["is_send"])
                || AsBool(raw["isOutgoing"])
                || string.Equals(ImportText.Clean(TryGetRaw(raw, "direction")), "outgoing", StringComparison.OrdinalIgnoreCase);
            if (!isSend)
            {
                continue;
            }

            var sender = ImportText.Clean(FirstNonEmpty(
                TryGetRaw(raw, "sender"),
                TryGetRaw(raw, "senderUsername"),
                TryGetRaw(raw, "senderWxid"),
                TryGetRaw(raw, "senderNativeId"),
                TryGetRaw(raw, "senderId"),
                TryGetRaw(raw, "platformId")));
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

        if (conversation.Kind == "private" && counts.Count > 1)
        {
            counts.Remove(conversation.NativeId);
            counter.RemoveAll(id => id == conversation.NativeId);
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

        return selfSender;
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        IEnumerable<JsonObject> messages,
        ParsedConversation conversation,
        string? selfSender,
        string filePath,
        IReadOnlyList<JsonObject>? members = null)
    {
        var memberDict = BuildMemberDictionary(members);
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var index = 0;
        foreach (var raw in messages)
        {
            yield return ParseMessage(raw, index++, conversation, selfSender, exportRoot, memberDict);
        }
    }

    public static IEnumerable<ParsedMessage> IterateJsonlMessages(
        string filePath,
        ParsedConversation conversation,
        string? selfSender,
        CancellationToken cancellationToken = default,
        Dictionary<string, JsonObject>? initialMembers = null)
    {
        var memberDict = initialMembers ?? new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var index = 0;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            JsonObject? raw;
            try
            {
                raw = JsonNode.Parse(trimmed) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (raw == null)
            {
                continue;
            }

            var typeTag = ImportText.Clean(TryGetRaw(raw, "_type")).ToLowerInvariant();
            if (typeTag == "header")
            {
                continue;
            }

            if (typeTag == "member")
            {
                var mId = ExtractMemberPlatformId(raw);
                if (mId.Length > 0)
                {
                    memberDict[mId] = raw;
                }

                continue;
            }

            yield return ParseMessage(raw, index++, conversation, selfSender, exportRoot, memberDict);
        }
    }

    private static ParsedMessage ParseMessage(
        JsonObject raw,
        int index,
        ParsedConversation conversation,
        string? selfSender,
        string exportRoot,
        IReadOnlyDictionary<string, JsonObject>? memberDict)
    {
        var rawContent = OrEmpty(ImportText.Clean(TryGetRaw(raw, "content")), "[空消息]");
        var exportedSender = ImportText.Clean(FirstNonEmpty(
            TryGetRaw(raw, "sender"),
            TryGetRaw(raw, "senderUsername"),
            TryGetRaw(raw, "senderWxid"),
            TryGetRaw(raw, "senderNativeId"),
            TryGetRaw(raw, "senderId"),
            TryGetRaw(raw, "platformId"),
            TryGetRaw(raw, "uid"),
            TryGetRaw(raw, "uin")));

        JsonObject? memberObj = null;
        if (exportedSender.Length > 0 && memberDict != null && memberDict.TryGetValue(exportedSender, out var foundMember))
        {
            memberObj = foundMember;
        }

        var exportedName = ImportText.Clean(FirstNonEmpty(
            TryGetRaw(raw, "accountName"),
            TryGetRaw(raw, "senderDisplayName"),
            TryGetRaw(raw, "senderNickname"),
            TryGetRaw(raw, "senderName"),
            TryGetRaw(raw, "name"),
            TryGetRaw(raw, "nickname"),
            TryGetRaw(raw, "displayName"),
            TryGetRaw(raw, "groupNickname")));

        string? memberGroupNickname = memberObj != null
            ? ImportText.Clean(FirstNonEmpty(TryGetRaw(memberObj, "groupNickname"), TryGetRaw(memberObj, "groupCard"), TryGetRaw(memberObj, "card")))
            : null;
        string? memberDisplayName = memberObj != null
            ? ImportText.Clean(FirstNonEmpty(TryGetRaw(memberObj, "displayName"), TryGetRaw(memberObj, "remark"), TryGetRaw(memberObj, "alias")))
            : null;
        string? memberAccountName = memberObj != null
            ? ImportText.Clean(FirstNonEmpty(TryGetRaw(memberObj, "accountName"), TryGetRaw(memberObj, "username"), TryGetRaw(memberObj, "name"), TryGetRaw(memberObj, "nickname")))
            : null;

        var isSend = AsBool(raw["isSend"])
            || AsBool(raw["is_send"])
            || AsBool(raw["isOutgoing"])
            || string.Equals(ImportText.Clean(TryGetRaw(raw, "direction")), "outgoing", StringComparison.OrdinalIgnoreCase);

        if (!isSend && !string.IsNullOrEmpty(selfSender) && string.Equals(exportedSender, selfSender, StringComparison.OrdinalIgnoreCase))
        {
            isSend = true;
        }

        var senderNative = isSend && !string.IsNullOrEmpty(selfSender)
            ? selfSender!
            : exportedSender.Length > 0 ? exportedSender : "unknown";

        string senderName;
        if (isSend)
        {
            senderName = FirstNonEmpty(
                exportedName,
                memberDisplayName ?? "",
                memberGroupNickname ?? "",
                memberAccountName ?? "",
                "我");
        }
        else
        {
            senderName = FirstNonEmpty(
                memberGroupNickname ?? "",
                TryGetRaw(raw, "groupNickname"),
                memberDisplayName ?? "",
                memberAccountName ?? "",
                exportedName,
                conversation.Kind == "private" ? conversation.Title : "",
                senderNative);
        }

        var typeNode = Get(raw, "type");
        var localTypeNode = Get(raw, "localType");
        var baseMessageType = ResolveMessageType(typeNode, localTypeNode, TryGetRaw(raw, "type"), rawContent);

        var (content, xmlMessageType, xmlSearchValues) = WeFlowParser.NormalizeContent(rawContent, exportedSender);
        var messageType = xmlMessageType ?? baseMessageType;

        var (declaredPath, sourcePath, filename) = ExtractMediaReference(
            raw, content, rawContent, exportRoot, messageType, conversation.Title);

        var attachments = new List<ParsedAttachment>();
        if (declaredPath is not null || sourcePath is not null)
        {
            var mediaKind = messageType != "other" && messageType != "text" ? messageType : "file";
            var mime = ImportText.GuessMime(sourcePath, filename);
            attachments.Add(new ParsedAttachment(
                Ordinal: 0,
                Kind: mediaKind,
                Filename: filename,
                DeclaredPath: declaredPath,
                SourcePath: sourcePath,
                DeclaredSize: ImportText.AsLong(raw["fileSize"]) ?? ImportText.AsLong(raw["size"]),
                MimeType: mime,
                Width: AsNullableInt(raw["imageWidth"] ?? raw["width"]),
                Height: AsNullableInt(raw["imageHeight"] ?? raw["height"]),
                Duration: ImportText.AsDouble(raw["voiceDuration"] ?? raw["duration"]),
                Metadata: new JsonObject()));
        }

        var timestamp = ImportText.AsLong(raw["timestamp"])
            ?? ImportText.AsLong(raw["createTime"])
            ?? ImportText.AsLong(raw["time"])
            ?? 0;
        var timestampMs = timestamp >= 10_000_000_000L ? timestamp : timestamp * 1000L;

        var localId = OrNull(ImportText.Clean(FirstNonEmpty(
            TryGetRaw(raw, "localId"),
            TryGetRaw(raw, "localID"),
            TryGetRaw(raw, "msgLocalId"))));
        var nativeId = OrNull(ImportText.Clean(FirstNonEmpty(
            TryGetRaw(raw, "id"),
            TryGetRaw(raw, "platformMessageId"),
            TryGetRaw(raw, "messageId"),
            TryGetRaw(raw, "msgId"),
            TryGetRaw(raw, "svrid"))));

        var replyToNativeId = ExtractReplyId(raw);

        var isSystem = messageType == "system" || AsBool(raw["isSystem"]);
        var isRecalled = (isSystem && (content.Contains("撤回", StringComparison.Ordinal) || rawContent.Contains("撤回", StringComparison.Ordinal)))
            || AsBool(raw["recalled"])
            || AsBool(raw["isRecalled"]);
        var direction = isSystem ? "system" : isSend ? "outgoing" : "incoming";
        var normalizedSearchText = BuildSearchText(raw, content, xmlSearchValues);

        var mediaFileName = sourcePath is null ? null : Path.GetFileName(sourcePath);
        var mediaType = attachments.Count > 0
            ? (messageType != "other" && messageType != "text" ? messageType : "file")
            : (messageType is "image" or "audio" or "video" or "file" or "emoji" ? messageType : null);

        var semantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
            ["local_type"] = LocalTypeString(localTypeNode ?? typeNode),
            ["media_type"] = NullStr(mediaType),
            ["message_type"] = messageType,
            ["content"] = content,
            ["media_name"] = NullStr(mediaFileName),
            ["reply_to_native_id"] = NullStr(replyToNativeId),
            ["search_text"] = normalizedSearchText,
        };

        var payloadHash = CanonicalJson.HashHex(semantic);
        var compatibleHashes = new HashSet<string>();
        if (mediaFileName is not null)
        {
            var clone = (JsonObject)semantic.DeepClone();
            clone["media_name"] = null;
            compatibleHashes.Add(CanonicalJson.HashHex(clone));
        }

        compatibleHashes.Remove(payloadHash);

        var senderAliases = new List<string>();
        AddUnique(senderAliases, senderName);
        if (!string.IsNullOrEmpty(exportedName))
        {
            AddUnique(senderAliases, exportedName);
        }

        if (!string.IsNullOrEmpty(memberDisplayName))
        {
            AddUnique(senderAliases, memberDisplayName);
        }

        if (!string.IsNullOrEmpty(memberGroupNickname))
        {
            AddUnique(senderAliases, memberGroupNickname);
        }

        if (!string.IsNullOrEmpty(memberAccountName))
        {
            AddUnique(senderAliases, memberAccountName);
        }

        AddUnique(senderAliases, senderNative);

        return new ParsedMessage(
            NativeId: nativeId,
            LocalId: localId,
            TimestampMs: timestampMs,
            Sequence: null,
            SenderNativeId: senderNative,
            SenderName: senderName,
            SenderAliases: senderAliases,
            Direction: direction,
            MessageType: messageType,
            MediaType: mediaType,
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
                ["local_type"] = LocalTypeString(localTypeNode ?? typeNode),
            }),
            SourceLocator: $"message:{index}:local:{localId ?? string.Empty}",
            RawPayload: raw,
            Attachments: attachments,
            CompatiblePayloadHashes: compatibleHashes
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToList());
    }

    internal static string ResolveMessageType(JsonNode? typeNode, JsonNode? localTypeNode, string? exportedType, string content)
    {
        var intType = ImportText.AsLong(typeNode);
        if (intType.HasValue && IntMessageTypes.TryGetValue((int)intType.Value, out var mappedInt))
        {
            return mappedInt;
        }

        var localCode = ImportText.AsLong(localTypeNode);
        if (localCode.HasValue)
        {
            switch (localCode.Value)
            {
                case 1: return "text";
                case 3: return "image";
                case 34: return "audio";
                case 43: return "video";
                case 47: return "emoji";
                case 48: return "location";
                case 2000:
                case 2001:
                case 419430449: return "transfer";
                case 10000:
                case 10002: return "system";
            }
        }

        if (!string.IsNullOrWhiteSpace(exportedType))
        {
            var cleaned = ImportText.Clean(exportedType);
            if (JsonMessageTypes.TryGetValue(cleaned, out var mapped))
            {
                return mapped;
            }
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

    private static (string? DeclaredPath, string? SourcePath, string? Filename) ExtractMediaReference(
        JsonObject raw,
        string content,
        string rawContent,
        string exportRoot,
        string messageType,
        string? sessionTitle)
    {
        var candidatePaths = new List<string>();

        foreach (var prop in new[] { "mediaPath", "filePath", "imagePath", "voicePath", "videoPath", "resourcePath", "localPath", "path", "url" })
        {
            var val = ImportText.Clean(TryGetRaw(raw, prop));
            if (val.Length > 0 && !val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidatePaths.Add(val);
            }
        }

        if (raw["media"] is JsonObject mediaObj)
        {
            foreach (var prop in new[] { "path", "localPath", "filePath", "url" })
            {
                var val = ImportText.Clean(TryGetRaw(mediaObj, prop));
                if (val.Length > 0 && !val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    candidatePaths.Add(val);
                }
            }
        }

        foreach (var (prefix, _) in BracketLabels)
        {
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                var remainder = content[prefix.Length..].Trim();
                if (remainder.Length > 0 && !remainder.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !remainder.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    candidatePaths.Add(remainder);
                }
            }

            if (rawContent.StartsWith(prefix, StringComparison.Ordinal))
            {
                var remainder = rawContent[prefix.Length..].Trim();
                if (remainder.Length > 0 && !remainder.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !remainder.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    candidatePaths.Add(remainder);
                }
            }
        }

        if (messageType is "image" or "audio" or "video" or "file" or "emoji")
        {
            foreach (var text in new[] { content, rawContent })
            {
                if ((text.Contains('/') || text.Contains('\\') || text.Contains('.'))
                    && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    candidatePaths.Add(text.Trim());
                }
            }
        }

        if (messageType == "file")
        {
            var fileCandidate = content.StartsWith("[文件]", StringComparison.Ordinal)
                ? content["[文件]".Length..].Trim()
                : content;
            fileCandidate = System.Net.WebUtility.HtmlDecode(fileCandidate).Trim();
            if (fileCandidate.Length > 0 && !fileCandidate.Contains('/') && !fileCandidate.Contains('\\') && !fileCandidate.Contains(':'))
            {
                var extension = Path.GetExtension(fileCandidate).TrimStart('.').ToLowerInvariant();
                if (extension.Length > 0 && extension.All(char.IsAsciiLetterOrDigit))
                {
                    var declared = $"media/files/{extension}/{fileCandidate}";
                    var resolved = ImportText.SafeResolveMedia(exportRoot, declared, sessionTitle);
                    if (resolved != null && File.Exists(resolved))
                    {
                        return (declared, resolved, fileCandidate);
                    }

                    candidatePaths.Add(declared);
                }
            }
        }

        foreach (var candidate in candidatePaths)
        {
            var normalized = candidate.Replace('\\', '/').Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            var filename = Path.GetFileName(normalized);
            var resolved = ImportText.SafeResolveMedia(exportRoot, normalized, sessionTitle);
            if (resolved != null && File.Exists(resolved))
            {
                return (normalized, resolved, filename.Length > 0 ? filename : null);
            }
        }

        foreach (var candidate in candidatePaths)
        {
            var normalized = candidate.Replace('\\', '/').Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            var filename = Path.GetFileName(normalized);
            var resolved = ImportText.SafeResolveMedia(exportRoot, normalized, sessionTitle);
            return (normalized, resolved, filename.Length > 0 ? filename : null);
        }

        return (null, null, null);
    }

    private static string? ExtractReplyId(JsonObject raw)
    {
        var replyTo = OrNull(FirstNonEmpty(
            ImportText.Clean(TryGetRaw(raw, "replyToMessageId")),
            ImportText.Clean(TryGetRaw(raw, "quotedSvrid")),
            ImportText.Clean(TryGetRaw(raw, "replyToId")),
            ImportText.Clean(TryGetRaw(raw, "replyTo"))));

        if (replyTo is null && raw["quote"] is JsonObject quoteObj)
        {
            replyTo = OrNull(FirstNonEmpty(
                ImportText.Clean(TryGetRaw(quoteObj, "sourceMessageId")),
                ImportText.Clean(TryGetRaw(quoteObj, "platformMessageId")),
                ImportText.Clean(TryGetRaw(quoteObj, "messageId")),
                ImportText.Clean(TryGetRaw(quoteObj, "id")),
                ImportText.Clean(TryGetRaw(quoteObj, "localId"))));
        }

        if (replyTo is null && raw["reply"] is JsonObject replyObj)
        {
            replyTo = OrNull(FirstNonEmpty(
                ImportText.Clean(TryGetRaw(replyObj, "sourceMessageId")),
                ImportText.Clean(TryGetRaw(replyObj, "platformMessageId")),
                ImportText.Clean(TryGetRaw(replyObj, "messageId")),
                ImportText.Clean(TryGetRaw(replyObj, "id")),
                ImportText.Clean(TryGetRaw(replyObj, "localId"))));
        }

        return replyTo;
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

        if (raw["quote"] is JsonObject quoteObj)
        {
            AddUnique(values, TryGetRaw(quoteObj, "content"));
            AddUnique(values, TryGetRaw(quoteObj, "text"));
        }

        if (raw["reply"] is JsonObject replyObj)
        {
            AddUnique(values, TryGetRaw(replyObj, "content"));
            AddUnique(values, TryGetRaw(replyObj, "text"));
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

            if (scalar.TryGetValue<int>(out var i))
            {
                return i != 0;
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
        return raw is "true" or "false" ? (raw == "true" ? "True" : "False") : raw.Trim('"');
    }

    private static JsonNode? NullStr(string? value)
    {
        return value is null ? null : JsonValue.Create(value);
    }

    private static int? AsNullableInt(JsonNode? node)
    {
        var parsed = ImportText.AsLong(node);
        return parsed.HasValue ? (int?)checked((int)Math.Clamp(parsed.Value, int.MinValue, int.MaxValue)) : null;
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
