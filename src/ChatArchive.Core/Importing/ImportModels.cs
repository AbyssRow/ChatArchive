using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

public sealed record ParsedConversation(
    string Platform,
    string AccountId,
    string NativeId,
    string Kind,
    string Title);

public sealed record ParsedAttachment(
    int Ordinal,
    string Kind,
    string? Filename,
    string? DeclaredPath,
    string? SourcePath,
    long? DeclaredSize,
    string? MimeType,
    int? Width,
    int? Height,
    double? Duration,
    JsonObject Metadata);

public sealed record ParsedMessage(
    string? NativeId,
    string? LocalId,
    long TimestampMs,
    string? Sequence,
    string SenderNativeId,
    string SenderName,
    IReadOnlyList<string> SenderAliases,
    string Direction,
    string MessageType,
    string? MediaType,
    string Content,
    string SearchText,
    bool IsRecalled,
    bool IsSystem,
    string? ReplyToNativeId,
    string PayloadHash,
    string SemanticHash,
    string SourceLocator,
    JsonNode RawPayload,
    IReadOnlyList<ParsedAttachment> Attachments,
    IReadOnlyList<string> CompatiblePayloadHashes);
