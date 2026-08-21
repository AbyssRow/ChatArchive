namespace ChatArchive.Core.Models;

public sealed record ConversationInfo(
    long Id,
    string Platform,
    string AccountId,
    string NativeId,
    string Kind,
    string Title,
    long? FirstMessageAt,
    long? LastMessageAt,
    long MessageCount,
    string? LastMessagePreview,
    long MissingMediaCount);

public sealed record ConversationDetail(
    ConversationInfo Conversation,
    IReadOnlyList<string> Aliases);
