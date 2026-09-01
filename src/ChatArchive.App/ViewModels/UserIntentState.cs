namespace ChatArchive.App.ViewModels;

internal readonly record struct ContactTargetSnapshot(
    long ContactId,
    string IdentityToken,
    string DisplayName)
{
    public bool IsCurrent(
        long? selectedContactId,
        string? selectedIdentityToken,
        long? loadedDetailId,
        string? loadedIdentityToken)
    {
        return !string.IsNullOrWhiteSpace(IdentityToken)
            && selectedContactId == ContactId
            && loadedDetailId == ContactId
            && string.Equals(
                selectedIdentityToken,
                IdentityToken,
                StringComparison.Ordinal)
            && string.Equals(
                loadedIdentityToken,
                IdentityToken,
                StringComparison.Ordinal);
    }
}

internal sealed class ExclusiveInteractionGate
{
    private int _entered;

    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref _entered, 1, 0) == 0;
    }

    public void Exit()
    {
        if (Interlocked.Exchange(ref _entered, 0) == 0)
        {
            throw new InvalidOperationException("The interaction gate is not owned.");
        }
    }
}
