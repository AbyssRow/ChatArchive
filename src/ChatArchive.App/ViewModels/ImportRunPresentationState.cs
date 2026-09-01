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

internal sealed class ImportRunCancellationState
{
    private readonly object _gate = new();
    private CancellationTokenSource? _source;
    private bool _cancellationRequested;

    public CancellationToken Begin()
    {
        lock (_gate)
        {
            if (_source is not null)
            {
                throw new InvalidOperationException("An import run is already active.");
            }

            _source = new CancellationTokenSource();
            _cancellationRequested = false;
            return _source.Token;
        }
    }

    public bool RequestCancellation()
    {
        lock (_gate)
        {
            if (_source is null || _cancellationRequested)
            {
                return false;
            }

            _cancellationRequested = true;
            _source.Cancel();
            return true;
        }
    }

    public void End()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
            _cancellationRequested = false;
        }

        source?.Dispose();
    }
}
