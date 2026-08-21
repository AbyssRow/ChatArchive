namespace ChatArchive.Core.Models;

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
    string Kind,
    long? SenderId,
    string SenderName,
    string Snippet,
    string MessageType,
    string Direction,
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
