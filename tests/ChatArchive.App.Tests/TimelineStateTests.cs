using ChatArchive.App.ViewModels;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class TimelineStateTests
{
    [Fact]
    public void Starting_another_conversation_invalidates_the_previous_request()
    {
        var state = new TimelineRequestState();

        var first = state.StartConversation(10);
        var second = state.StartConversation(20);

        Assert.False(state.IsCurrent(first));
        Assert.True(state.IsCurrent(second));
        Assert.Equal(20, second.ConversationId);
        Assert.Null(second.Cursor);
    }

    [Fact]
    public void Search_context_sets_conversation_and_older_cursor()
    {
        var state = new TimelineRequestState();
        var messages = new[]
        {
            Message(7, 1_700_000_000_000),
            Message(8, 1_700_000_001_000),
        };
        var context = new MessageContext(42, "目标会话", 8, messages);

        var request = state.StartContext(context);

        Assert.Equal(42, request.ConversationId);
        Assert.Equal(CursorCodec.Encode(messages[0].TimestampMs, messages[0].Id), request.Cursor);
        Assert.True(state.IsCurrent(request));
    }

    [Fact]
    public void Initial_bottom_request_waits_until_the_list_can_be_positioned()
    {
        var state = new TimelineInitialPositionState();

        state.RequestBottom();

        Assert.False(state.TryTakeBottomRequest(canPosition: false));
        Assert.True(state.TryTakeBottomRequest(canPosition: true));
        Assert.False(state.TryTakeBottomRequest(canPosition: true));
    }

    private static MessageItem Message(long id, long timestampMs)
    {
        return new MessageItem(
            id, 42, null, "Alice", "incoming", "text", null,
            $"消息{id}", false, false, timestampMs, Array.Empty<AttachmentInfo>());
    }
}
