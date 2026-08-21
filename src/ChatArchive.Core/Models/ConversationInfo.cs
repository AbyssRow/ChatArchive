namespace ChatArchive.Core.Models;

public sealed record ConversationInfo(
    long Id,
    string Platform,
    string Kind,
    string Title,
    long? FirstMessageAt,
    long? LastMessageAt,
    long MessageCount);
