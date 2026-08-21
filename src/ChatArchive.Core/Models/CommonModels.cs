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
    long AvailableAttachments,
    long MissingAttachments,
    long MediaFileCount,
    long MediaTotalBytes,
    long? FirstMessageAt,
    long? LastMessageAt);
