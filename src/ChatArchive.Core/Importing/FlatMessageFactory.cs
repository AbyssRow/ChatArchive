using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

internal sealed record FlatMessageData(
    string? NativeId,
    string? LocalId,
    long TimestampMs,
    string SenderNativeId,
    string SenderName,
    string Direction,
    string MessageType,
    string Content,
    string SourceLocator,
    JsonObject RawPayload,
    IReadOnlyList<ParsedAttachment>? Attachments = null,
    string? Sequence = null,
    string? ReplyToNativeId = null,
    bool IsRecalled = false,
    bool IsSystem = false,
    string? MediaType = null);

internal static class FlatMessageFactory
{
    internal static string SyntheticSenderNativeId(string conversationNativeId, string senderName)
    {
        var normalizedName = senderName.Trim();
        if (normalizedName.Length == 0)
        {
            normalizedName = "unknown";
        }

        var value = string.Concat(conversationNativeId, "\0", normalizedName);
        return "synthetic:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static ParsedMessage Create(FlatMessageData data)
    {
        var aliases = new List<string>();
        AddAlias(aliases, data.SenderName);
        AddAlias(aliases, data.SenderNativeId);

        var payload = new JsonObject
        {
            ["timestamp_ms"] = data.TimestampMs,
            ["sender"] = data.SenderNativeId,
            ["direction"] = data.Direction,
            ["message_type"] = data.MessageType,
            ["content"] = data.Content,
            ["search_text"] = data.Content,
        };

        return new ParsedMessage(
            data.NativeId,
            data.LocalId,
            data.TimestampMs,
            data.Sequence,
            data.SenderNativeId,
            data.SenderName,
            aliases,
            data.Direction,
            data.MessageType,
            data.MediaType,
            data.Content,
            data.Content,
            data.IsRecalled,
            data.IsSystem,
            data.ReplyToNativeId,
            CanonicalJson.HashHex(payload),
            CanonicalJson.HashHex(new JsonObject
            {
                ["timestamp_ms"] = data.TimestampMs,
                ["sender"] = data.SenderNativeId,
                ["direction"] = data.Direction,
            }),
            data.SourceLocator,
            data.RawPayload,
            data.Attachments ?? Array.Empty<ParsedAttachment>(),
            Array.Empty<string>());
    }

    private static void AddAlias(List<string> aliases, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            aliases.Add(value);
        }
    }
}
