using ChatArchive.App.ViewModels;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class ConversationListActivationTests
{
    [Fact]
    public void Ignores_programmatic_apply()
    {
        Assert.False(ConversationListActivation.IsUserActivation(
            applyInFlight: true,
            currentSelectedId: 1,
            addedId: 2,
            addedCount: 1));
    }

    [Fact]
    public void Ignores_collection_reset()
    {
        Assert.False(ConversationListActivation.IsUserActivation(
            applyInFlight: false,
            currentSelectedId: 1,
            addedId: null,
            addedCount: 0));
    }

    [Fact]
    public void Ignores_reattach_of_same_id()
    {
        Assert.False(ConversationListActivation.IsUserActivation(
            applyInFlight: false,
            currentSelectedId: 8,
            addedId: 8,
            addedCount: 1));
    }

    [Fact]
    public void Accepts_user_selecting_another_conversation()
    {
        Assert.True(ConversationListActivation.IsUserActivation(
            applyInFlight: false,
            currentSelectedId: 8,
            addedId: 9,
            addedCount: 1));
    }

    [Fact]
    public void Accepts_first_selection_when_none_selected()
    {
        Assert.True(ConversationListActivation.IsUserActivation(
            applyInFlight: false,
            currentSelectedId: null,
            addedId: 3,
            addedCount: 1));
    }
}
