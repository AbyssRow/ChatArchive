namespace ChatArchive.Core.Models;

public sealed record SenderProfile(
    long Id,
    string Platform,
    string NativeId,
    string CurrentName,
    bool IsSelf,
    IReadOnlyList<AliasInfo> Aliases,
    IReadOnlyList<SenderConversationInfo> Conversations);
