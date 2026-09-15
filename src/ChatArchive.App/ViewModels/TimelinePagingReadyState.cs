namespace ChatArchive.App.ViewModels;

public sealed class TimelinePagingReadyState
{
    private bool _pendingFocus;

    public bool IsReady { get; private set; }

    public void Reset()
    {
        _pendingFocus = false;
        IsReady = false;
    }

    public void BeginFocusJump()
    {
        _pendingFocus = true;
        IsReady = false;
    }

    public void MarkReady()
    {
        _pendingFocus = false;
        IsReady = true;
    }

    public bool OnViewChanged(bool isIntermediate, bool offsetReported)
    {
        if (!_pendingFocus || isIntermediate || !offsetReported)
        {
            return false;
        }

        _pendingFocus = false;
        IsReady = true;
        return true;
    }
}
