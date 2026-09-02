using System.Runtime.InteropServices;

namespace ChatArchive.App.Services;

internal static class PickerInterop
{
    public static nint RequireHandle(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new InvalidOperationException("窗口尚未准备好，无法打开系统文件夹选择框。请再试一次。");
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
