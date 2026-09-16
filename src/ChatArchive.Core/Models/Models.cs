namespace ChatArchive.Core.Models;

public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record MessageContext(
    long ConversationId,
    string ConversationTitle,
    long FocusMessageId,
    IReadOnlyList<MessageItem> Messages);

public sealed record ArchiveStats(
    long TotalMessages,
    long QQMessages,
    long WeChatMessages,
    long TotalConversations,
    long PrivateConversations,
    long GroupConversations,
    long SenderCount,
    long AttachmentCount,
    long MissingAttachments,
    long MediaFileCount,
    long MediaTotalBytes);

public sealed record AttachmentInfo(
    long Id,
    int Ordinal,
    string Kind,
    string? Filename,
    bool IsAvailable,
    string? MimeType,
    int? Width,
    int? Height,
    double? Duration,
    string? DeclaredPath,
    string? ManagedPath,
    string? SourcePath,
    string? MediaSha256);

public sealed record MessageItem(
    long Id,
    long ConversationId,
    long? SenderId,
    string SenderName,
    string Direction,
    string MessageType,
    string? MediaType,
    string Content,
    bool IsRecalled,
    bool IsSystem,
    long TimestampMs,
    IReadOnlyList<AttachmentInfo> Attachments,
    string? CustomAvatarPath = null,
    string? AccountLabel = null);

public sealed record ConversationInfo(
    long Id,
    string Platform,
    string Kind,
    string Title,
    long? LastMessageAt,
    string? LastMessagePreview);

public sealed record AliasInfo(
    string Alias,
    long? LastSeenAt);

public sealed record SenderConversationInfo(
    long ConversationId,
    string Title,
    long MessageCount);

public sealed record SenderProfile(
    long Id,
    string Platform,
    string NativeId,
    string? QQNumber,
    string CurrentName,
    IReadOnlyList<AliasInfo> Aliases,
    IReadOnlyList<SenderConversationInfo> Conversations);

public sealed record ContactInfo(
    long Id,
    string DisplayName,
    string? CustomAvatarPath,
    string? Note,
    long MessageCount,
    string IdentityToken);

public sealed record BoundSenderInfo(
    long SenderId,
    string Platform,
    string NativeId,
    string? QQNumber,
    string OriginalName,
    string? AccountLabel,
    bool IsPrimary,
    long MessageCount,
    string? BoundContactName = null,
    long? BoundContactId = null,
    string? BoundContactIdentityToken = null);

public sealed record ContactDetail(
    long Id,
    string DisplayName,
    string? CustomAvatarPath,
    string? Note,
    IReadOnlyList<BoundSenderInfo> Senders,
    IReadOnlyList<SenderConversationInfo> Conversations,
    long TotalMessageCount,
    string IdentityToken);

public enum SearchMode
{
    Empty,
    Fts,
    Substring,
}

public sealed record SearchHit(
    long MessageId,
    long ConversationId,
    string ConversationTitle,
    string Platform,
    string SenderName,
    string Snippet,
    long TimestampMs);

public sealed record SearchHitPage(
    IReadOnlyList<SearchHit> Items,
    string? NextCursor,
    SearchMode Mode);

public sealed record SearchFilter(
    string? Platform = null,
    string? Kind = null,
    long? ConversationId = null,
    string? Sender = null,
    string? MessageType = null,
    long? DateFromMs = null,
    long? DateToExclusiveMs = null);

public sealed record FilterOptionItem(string Value, long Amount);

public sealed record FilterOptions(
    IReadOnlyList<FilterOptionItem> MessageTypes,
    IReadOnlyList<FilterOptionItem> Senders);
