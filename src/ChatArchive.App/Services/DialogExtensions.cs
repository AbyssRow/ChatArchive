using Microsoft.UI.Xaml.Controls;

namespace ChatArchive.App.Services;

public static class DialogExtensions
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    public static async Task<ContentDialogResult> ShowSafeAsync(this ContentDialog dialog)
    {
        await DialogGate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            DialogGate.Release();
        }
    }
}
