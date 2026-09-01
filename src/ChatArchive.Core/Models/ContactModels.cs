namespace ChatArchive.Core.Models;

public sealed record ContactInfo(
    long Id,
    string DisplayName,
    string? CustomAvatarPath,
    string? Note,
    long MessageCount,
    long CreatedAtMs,
    long UpdatedAtMs)
{
    public string IdentityToken { get; init; } = string.Empty;
}

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
    public string? BoundContactIdentityToken { get; init; }
}

public sealed record ContactDetail(
    long Id,
    string DisplayName,
    string? CustomAvatarPath,
    string? Note,
    IReadOnlyList<BoundSenderInfo> Senders,
    IReadOnlyList<SenderConversationInfo> Conversations,
    long TotalMessageCount)
{
    public string IdentityToken { get; init; } = string.Empty;
}
