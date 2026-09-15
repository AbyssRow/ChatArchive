namespace ChatArchive.App.ViewModels;

public sealed class TimelinePagingReadyState
{
    private bool _pendingFocus;
    private bool _scrollIssued;

    public bool IsReady { get; private set; }

    public bool ScrollIssued => _scrollIssued;

    public void Reset()
    {
        _pendingFocus = false;
        _scrollIssued = false;
        IsReady = false;
    }

    public void BeginFocusJump()
    {
        _pendingFocus = true;
        _scrollIssued = false;
        IsReady = false;
    }

    public void NoteScrollIssued()
    {
        _scrollIssued = true;
    }

    public void MarkReady()
    {
        _pendingFocus = false;
        _scrollIssued = false;
        IsReady = true;
    }

    public bool OnViewChanged(bool isIntermediate, bool offsetReported)
    {
        if (!_pendingFocus || !_scrollIssued || isIntermediate || !offsetReported)
        {
            return false;
        }

        _pendingFocus = false;
        IsReady = true;
        return true;
    }
}
