namespace ChatArchive.App.ViewModels;

internal sealed class SearchOptionsReloadGate
{
    private bool _isPending;
    private long? _ownerGeneration;

    public bool IsLocked => _isPending || _ownerGeneration.HasValue;

    public void Begin()
    {
        if (_isPending)
        {
            throw new InvalidOperationException("A search options reload is already awaiting ownership.");
        }

        _isPending = true;
        _ownerGeneration = null;
    }

    public void Own(long generation)
    {
        if (!_isPending)
        {
            throw new InvalidOperationException("Begin must be called before assigning reload ownership.");
        }

        _isPending = false;
        _ownerGeneration = generation;
    }

    public bool TryRelease(long generation)
    {
        if (_ownerGeneration != generation)
        {
            return false;
        }

        _ownerGeneration = null;
        return true;
    }

    public void CancelPending()
    {
        if (!_isPending)
        {
            throw new InvalidOperationException("Only a pending reload can be canceled before ownership.");
        }

        _isPending = false;
    }
}
