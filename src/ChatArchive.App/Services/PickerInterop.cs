using System.Runtime.InteropServices;

namespace ChatArchive.App.Services;

internal static class PickerInterop
{
    internal static Func<nint, bool>? HandleValidator { get; set; }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    public static bool IsUsableHandle(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        return HandleValidator?.Invoke(hwnd) ?? IsWindow(hwnd);
    }

    public static nint RequireHandle(nint hwnd)
    {
        if (!IsUsableHandle(hwnd))
        {
            throw new InvalidOperationException("窗口尚未准备好，无法打开系统选择框。请再试一次。");
        }

        return hwnd;
    }

    public static string FormatFailure(string action, Exception ex)
    {
        if (ex is COMException com)
        {
            return $"{action}失败: {com.Message} (0x{com.HResult:X8})";
        }

        return $"{action}失败: {ex.Message}";
    }
}
