namespace ChatArchive.Core.Models;

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
