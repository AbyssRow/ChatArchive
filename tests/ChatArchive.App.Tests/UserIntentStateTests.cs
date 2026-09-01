using ChatArchive.App.ViewModels;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class UserIntentStateTests
{
    [Theory]
    [InlineData(42L, 42L, true)]
    [InlineData(7L, 42L, false)]
    [InlineData(null, 42L, false)]
    [InlineData(42L, null, false)]
    public void Contact_target_requires_the_same_selected_and_loaded_contact(
        long? selectedContactId,
        long? loadedDetailId,
        bool expected)
    {
        var target = new ContactTargetSnapshot(42, "target-token", "目标联系人");

        Assert.Equal(
            expected,
            target.IsCurrent(
                selectedContactId,
                "target-token",
                loadedDetailId,
                "target-token"));
    }

    [Theory]
    [InlineData("replacement-token", "target-token")]
    [InlineData("target-token", "replacement-token")]
    [InlineData(null, "target-token")]
    [InlineData("target-token", null)]
    public void Contact_target_rejects_reused_identity_even_when_ids_match(
        string? selectedIdentityToken,
        string? loadedIdentityToken)
    {
        var target = new ContactTargetSnapshot(42, "target-token", "目标联系人");

        Assert.False(target.IsCurrent(
            42,
            selectedIdentityToken,
            42,
            loadedIdentityToken));
    }

    [Fact]
    public void Exclusive_interaction_rejects_reentry_until_the_owner_finishes()
    {
        var gate = new ExclusiveInteractionGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.Exit();

        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void Exclusive_interaction_can_be_reentered_after_owner_cleanup_on_failure()
    {
        var gate = new ExclusiveInteractionGate();

        try
        {
            Assert.True(gate.TryEnter());
            throw new InvalidOperationException("simulated action failure");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            gate.Exit();
        }

        Assert.True(gate.TryEnter());
    }
}
