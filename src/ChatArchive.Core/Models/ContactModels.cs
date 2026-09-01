namespace ChatArchive.Core.Models;

public sealed record ContactInfo(
    long Id,
    string DisplayName,
    string? CustomAvatarPath,
    string? Note,
    long MessageCount,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record BoundSenderInfo(
    long SenderId,
    string Platform,
    string NativeId,
    string? QQNumber,
    string OriginalName,
    string? AccountLabel,
    bool IsPrimary,
    long MessageCount,
    string? BoundContactName = null)
{
    public long? BoundContactId { get; init; }
}

public sealed record ContactDetail(
    long Id,
    string DisplayName,
    string? CustomAvatarPath,
    string? Note,
    IReadOnlyList<BoundSenderInfo> Senders,
    IReadOnlyList<SenderConversationInfo> Conversations,
    long TotalMessageCount);
