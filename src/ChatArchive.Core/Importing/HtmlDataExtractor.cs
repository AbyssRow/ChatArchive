using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

/// <summary>
/// HTML 内嵌数据提取解析器，支持 WeFlow、CipherTalk、QQ Chat Exporter 及 ChatLab 等生成的 HTML 导出。
/// </summary>
public static partial class HtmlDataExtractor
{
    [GeneratedRegex(@"<script\b[^>]*\bid\s*=\s*[""'](?:__WEFLOW_DATA__|ciphertalk-data|__DATA__|__CHAT_DATA__)[""'][^>]*>(?<payload>[\s\S]*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"window\.(?:__DATA__|__CIPHERTALK_DATA__|__CHAT_DATA__|__WEFLOW_DATA__)\s*=\s*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WindowVarRegex();

    /// <summary>
    /// 从 HTML 内容中提取内嵌的 JSON 字符串 payload。
    /// </summary>
    public static string? ExtractJsonPayload(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return null;
        }

        // 1. 探测 <script id="__WEFLOW_DATA__|ciphertalk-data|__DATA__|__CHAT_DATA__" ...>...</script>
        var scriptMatch = ScriptTagRegex().Match(htmlContent);
        if (scriptMatch.Success)
        {
            var rawPayload = scriptMatch.Groups["payload"].Value.Trim();
            if (rawPayload.Length > 0)
            {
                if (rawPayload.StartsWith('{') || rawPayload.StartsWith('['))
                {
                    return rawPayload;
                }

                // 脚本内部可能也是 window.__DATA__ = {...}; 形式
                var innerVar = ExtractJsonFromWindowAssignment(rawPayload);
                if (innerVar != null)
                {
                    return innerVar;
                }

                return rawPayload;
            }
        }

        // 2. 探测 window.__DATA__ / window.__CIPHERTALK_DATA__ / window.__CHAT_DATA__ = {...};
        var windowVarPayload = ExtractJsonFromWindowAssignment(htmlContent);
        if (windowVarPayload != null)
        {
            return windowVarPayload;
        }

        return null;
    }

    /// <summary>
    /// 从 HTML 文件中提取内嵌的 JSON 字符串 payload。
    /// </summary>
    public static string? ExtractJsonPayloadFromFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var content = reader.ReadToEnd();
            return ExtractJsonPayload(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断指定 HTML 文件是否包含受支持的内嵌数据标记。
    /// </summary>
    public static bool HasEmbeddedPayload(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            var buffer = new char[64 * 1024];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return false;
            }

            var chunk = new string(buffer, 0, read);
            if (ScriptTagRegex().IsMatch(chunk) || WindowVarRegex().IsMatch(chunk))
            {
                return true;
            }

            var read2 = reader.Read(buffer, 0, buffer.Length);
            if (read2 > 0)
            {
                var overlap = chunk.Substring(Math.Max(0, chunk.Length - 1024));
                var chunk2 = overlap + new string(buffer, 0, read2);
                if (ScriptTagRegex().IsMatch(chunk2) || WindowVarRegex().IsMatch(chunk2))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从 HTML 文件中提取内嵌数据并智能路由到对应的解析器构造 ExportFile。
    /// </summary>
    public static ExportFile ExtractAndRoute(string filePath, CancellationToken cancellationToken = default)
    {
        var jsonPayload = ExtractJsonPayloadFromFile(filePath)
            ?? throw new ImportFormatException(filePath, "未在 HTML 文件中找到有效的内嵌聊天数据");

        return ExtractAndRoutePayload(jsonPayload, filePath, cancellationToken);
    }

    /// <summary>
    /// 根据内嵌 JSON 根结构智能路由到对应解析逻辑。
    /// </summary>
    public static ExportFile ExtractAndRoutePayload(string jsonPayload, string filePath, CancellationToken cancellationToken = default)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(jsonPayload);
        }
        catch (JsonException ex)
        {
            throw new ImportFormatException(filePath, $"内嵌 JSON 解析失败（{ex.Message}）");
        }

        if (rootNode is not JsonObject rootObj)
        {
            throw new ImportFormatException(filePath, "内嵌数据必须是 JSON 对象");
        }

        // 1. ChatLab 路由 (chatlab + meta)
        if (rootObj["chatlab"] is JsonObject chatlab && rootObj["meta"] is JsonObject meta)
        {
            var version = ImportText.Clean(chatlab["version"]);
            if (!string.Equals(version, "0.0.2", StringComparison.Ordinal))
            {
                throw new ImportFormatException(
                    filePath,
                    $"不支持的 ChatLab 导出版本 {Display(version)}；支持版本 0.0.2");
            }

            List<JsonObject>? members = null;
            if (rootObj["members"] is JsonArray membersArray)
            {
                members = membersArray.OfType<JsonObject>().ToList();
            }

            var conversation = ChatLabParser.ReadConversation(meta, filePath, members);
            var ownerId = ImportText.Clean(FirstNonEmpty(
                ImportText.Clean(meta["ownerId"]),
                ImportText.Clean(meta["ownerID"]),
                ImportText.Clean(meta["selfWxid"]),
                ImportText.Clean(meta["selfId"]),
                ImportText.Clean(meta["accountId"])));

            var messages = (rootObj["messages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var selfSender = !string.IsNullOrEmpty(ownerId)
                ? ownerId
                : ChatLabParser.InferSelfSender(messages, conversation, cancellationToken);

            return new ExportFile(
                conversation,
                token => ChatLabParser.IterateMessages(messages, conversation, selfSender, filePath, members));
        }

        // 2. QQ Chat Exporter 路由 (chatInfo)
        if (rootObj["chatInfo"] is JsonObject chat)
        {
            JsonObject? metadata = rootObj["metadata"] as JsonObject ?? rootObj["exporter"] as JsonObject;
            if (metadata != null)
            {
                var exporterName = ImportText.Clean(metadata["name"]);
                if (!string.IsNullOrEmpty(exporterName) && !exporterName.Contains("QQChatExporter", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ImportFormatException(
                        filePath,
                        $"QQ 导出器标识无效：应为 QQChatExporter，实际为 {Display(exporterName)}");
                }
            }

            var conversation = QqParser.ReadConversation(chat, filePath);
            var selfUid = ImportText.Clean(chat["selfUid"]);
            var selfUin = ImportText.Clean(chat["selfUin"]);
            var messages = (rootObj["messages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

            return new ExportFile(
                conversation,
                token => QqParser.IterateMessages(messages, conversation, filePath, selfUid, selfUin));
        }

        // 3. CipherTalk 路由 (exportInfo + session)
        if (rootObj["exportInfo"] is JsonObject exportInfo)
        {
            var generator = ImportText.Clean(exportInfo["generator"]);
            var format = ImportText.Clean(exportInfo["format"]);
            if (!generator.Contains("CipherTalk", StringComparison.OrdinalIgnoreCase)
                && !format.Contains("detailed-json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ImportFormatException(
                    filePath,
                    $"CipherTalk 导出器标识无效：generator={generator}, format={format}");
            }

            if (rootObj["session"] is not JsonObject session)
            {
                throw new ImportFormatException(filePath, "CipherTalk 缺少 session 对象");
            }

            var conversation = CipherTalkParser.ReadConversation(session, filePath);
            var ownerId = ImportText.Clean(session["ownerId"]);
            if (string.IsNullOrEmpty(ownerId))
            {
                ownerId = ImportText.Clean(session["ownerID"]);
            }

            var messages = (rootObj["messages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var selfSender = !string.IsNullOrEmpty(ownerId)
                ? ownerId
                : CipherTalkParser.InferSelfSender(messages, conversation, cancellationToken);

            return new ExportFile(
                conversation,
                token => CipherTalkParser.IterateMessages(messages, conversation, selfSender, filePath));
        }

        // 4. WeFlow 路由 (session)
        if (rootObj["session"] is JsonObject weflowSession)
        {
            var conversation = WeFlowParser.ReadConversation(weflowSession, filePath);

            Dictionary<int, JsonObject>? senders = null;
            if (rootObj["senders"] is JsonArray sendersArray)
            {
                senders = new Dictionary<int, JsonObject>();
                foreach (var senderObj in sendersArray.OfType<JsonObject>())
                {
                    var id = ImportText.AsLong(senderObj["senderID"]) ?? ImportText.AsLong(senderObj["senderId"]);
                    if (id.HasValue)
                    {
                        senders[(int)id.Value] = senderObj;
                    }
                }
            }

            var messages = (rootObj["messages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var selfSender = WeFlowParser.InferSelfSender(messages, conversation, cancellationToken, senders);

            return new ExportFile(
                conversation,
                token => WeFlowParser.IterateMessages(messages, conversation, selfSender, filePath, senders));
        }

        throw new ImportFormatException(filePath, "无法识别内嵌 HTML 数据的聊天导出格式");
    }

    private static string? ExtractJsonFromWindowAssignment(string content)
    {
        var match = WindowVarRegex().Match(content);
        if (!match.Success)
        {
            return null;
        }

        var startIndex = match.Index + match.Length;
        while (startIndex < content.Length && char.IsWhiteSpace(content[startIndex]))
        {
            startIndex++;
        }

        if (startIndex >= content.Length || content[startIndex] != '{')
        {
            return null;
        }

        // 括号配对提取完整的 JSON 对象
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = startIndex; i < content.Length; i++)
        {
            var c = content[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return content.Substring(startIndex, i - startIndex + 1);
                    }
                }
            }
        }

        return null;
    }

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";

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
