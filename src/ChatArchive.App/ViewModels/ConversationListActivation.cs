namespace ChatArchive.App.ViewModels;

internal static class ConversationListActivation
{
    public static bool IsUserActivation(
        bool applyInFlight,
        long? currentSelectedId,
        long? addedId,
        int addedCount)
    {
        if (applyInFlight || addedCount == 0 || addedId is null)
        {
            return false;
        }

        return currentSelectedId != addedId;
    }
}
