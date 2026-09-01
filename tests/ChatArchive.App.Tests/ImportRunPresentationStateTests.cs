using ChatArchive.App.ViewModels;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class ImportRunPresentationStateTests
{
    [Fact]
    public void Current_generation_progress_is_accepted_before_termination()
    {
        var state = new ImportRunPresentationState();

        var generation = state.Begin();

        Assert.True(state.CanApplyProgress(generation));
        Assert.False(state.IsCurrentTerminal(generation));
    }

    [Theory]
    [InlineData(ImportPhase.Importing)]
    [InlineData(ImportPhase.Done)]
    public void Progress_after_success_termination_cannot_overwrite_detailed_summary(ImportPhase phase)
    {
        var state = new ImportRunPresentationState();
        var generation = state.Begin();
        var displayedStatus = "working";

        Assert.True(state.TryTerminate(generation));
        displayedStatus = "detailed success summary";

        if (state.CanApplyProgress(generation))
        {
            displayedStatus = phase.ToString();
        }

        Assert.Equal("detailed success summary", displayedStatus);
    }

    [Fact]
    public void Progress_after_failure_termination_is_rejected()
    {
        var state = new ImportRunPresentationState();
        var generation = state.Begin();

        Assert.True(state.TryTerminate(generation));

        Assert.False(state.CanApplyProgress(generation));
    }

    [Fact]
    public void Current_generation_terminates_exactly_once()
    {
        var state = new ImportRunPresentationState();
        var generation = state.Begin();

        Assert.True(state.TryTerminate(generation));
        Assert.False(state.TryTerminate(generation));
        Assert.True(state.IsCurrentTerminal(generation));
    }

    [Fact]
    public void Beginning_a_new_generation_rejects_all_old_generation_queries()
    {
        var state = new ImportRunPresentationState();
        var first = state.Begin();

        var second = state.Begin();

        Assert.False(state.CanApplyProgress(first));
        Assert.False(state.TryTerminate(first));
        Assert.True(state.TryTerminate(second));
        Assert.False(state.IsCurrentTerminal(first));
        Assert.True(state.IsCurrentTerminal(second));
    }

    [Fact]
    public void Only_current_generation_terminal_callback_can_publish()
    {
        var state = new ImportRunPresentationState();
        var first = state.Begin();
        Assert.True(state.TryTerminate(first));

        var second = state.Begin();
        Assert.True(state.TryTerminate(second));
        var publishedStatuses = new List<string>();

        if (state.IsCurrentTerminal(first))
        {
            publishedStatuses.Add("first");
        }

        if (state.IsCurrentTerminal(second))
        {
            publishedStatuses.Add("second");
        }

        Assert.Equal(new[] { "second" }, publishedStatuses);
    }
}
