namespace ChatArchive.Core.Models;

public sealed record AliasInfo(
    string Alias,
    long? ConversationId,
    long? FirstSeenAt,
    long? LastSeenAt);
