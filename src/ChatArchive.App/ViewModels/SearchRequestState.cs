using System.Globalization;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;

namespace ChatArchive.App.ViewModels;

public readonly record struct SearchRequest(
    long Generation,
    string Query,
    SearchFilter Filter,
    string? Cursor);

public sealed class SearchRequestState
{
    private long _generation;

    public SearchRequest Current { get; private set; }
    public bool HasMore => Current.Cursor is not null;

    public SearchRequest Start(string query, SearchFilter filter)
    {
        Current = new SearchRequest(++_generation, query, filter, null);
        return Current;
    }

    public SearchRequest? Continue()
    {
        return HasMore ? Current : null;
    }

    public bool ApplyPage(SearchRequest request, string? nextCursor)
    {
        if (!IsCurrent(request))
        {
            return false;
        }

        Current = request with { Cursor = nextCursor };
        return true;
    }

    public bool IsCurrent(SearchRequest request)
    {
        return request.Generation == Current.Generation;
    }

    public void Clear()
    {
        Current = new SearchRequest(++_generation, string.Empty, new SearchFilter(), null);
    }
}

public static class SearchFilterBuilder
{
    public static SearchFilter Build(
        string? platform,
        string? kind,
        long? conversationId,
        string? sender,
        string? messageType,
        DateTimeOffset? dateFrom,
        DateTimeOffset? dateTo)
    {
        return new SearchFilter(
            Platform: EmptyToNull(platform),
            Kind: EmptyToNull(kind),
            ConversationId: conversationId,
            Sender: EmptyToNull(sender),
            MessageType: EmptyToNull(messageType),
            DateFromMs: DateUtil.DateToStartMs(FormatDate(dateFrom)),
            DateToExclusiveMs: DateUtil.DateToExclusiveEndMs(FormatDate(dateTo)));
    }

    private static string? FormatDate(DateTimeOffset? value)
    {
        return value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
