namespace ChatArchive.App.ViewModels;

internal sealed class ImportRunPresentationState
{
    private readonly object _gate = new();
    private long _generation;
    private bool _terminal;

    public long Begin()
    {
        lock (_gate)
        {
            _generation++;
            _terminal = false;
            return _generation;
        }
    }

    public bool CanApplyProgress(long generation)
    {
        lock (_gate)
        {
            return generation == _generation && !_terminal;
        }
    }

    public bool TryTerminate(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _terminal)
            {
                return false;
            }

            _terminal = true;
            return true;
        }
    }

    public bool IsCurrentTerminal(long generation)
    {
        lock (_gate)
        {
            return generation == _generation && _terminal;
        }
    }
}
