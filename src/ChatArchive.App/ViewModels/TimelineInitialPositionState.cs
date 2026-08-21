namespace ChatArchive.App.ViewModels;

public sealed class TimelineInitialPositionState
{
    private bool _bottomRequested;

    public void RequestBottom()
    {
        _bottomRequested = true;
    }

    public bool TryTakeBottomRequest(bool canPosition)
    {
        if (!_bottomRequested || !canPosition)
        {
            return false;
        }

        _bottomRequested = false;
        return true;
    }
}
