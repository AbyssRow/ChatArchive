namespace ChatArchive.Core.Models;

public sealed record SenderConversationInfo(
    long ConversationId,
    string Title,
    string NameInConversation,
    long MessageCount,
    long? FirstMessageAt,
    long? LastMessageAt);
