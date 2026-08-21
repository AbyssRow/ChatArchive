using ChatArchive.Core.Data;
using ChatArchive.Core.Models;

namespace ChatArchive.App.ViewModels;

public readonly record struct TimelineRequest(
    long Generation,
    long ConversationId,
    string? Cursor);

public sealed class TimelineRequestState
{
    private long _generation;

    public TimelineRequest Current { get; private set; }

    public TimelineRequest StartConversation(long conversationId)
    {
        Current = new TimelineRequest(++_generation, conversationId, null);
        return Current;
    }

    public TimelineRequest StartContext(MessageContext context)
    {
        var cursor = context.Messages.Count > 0
            ? CursorCodec.Encode(context.Messages[0].TimestampMs, context.Messages[0].Id)
            : null;
        Current = new TimelineRequest(++_generation, context.ConversationId, cursor);
        return Current;
    }

    public TimelineRequest UpdateCursor(string? cursor)
    {
        Current = Current with { Cursor = cursor };
        return Current;
    }

    public bool IsCurrent(TimelineRequest request)
    {
        return request.Generation == Current.Generation
            && request.ConversationId == Current.ConversationId;
    }

    public void Clear()
    {
        Current = new TimelineRequest(++_generation, 0, null);
    }
}
