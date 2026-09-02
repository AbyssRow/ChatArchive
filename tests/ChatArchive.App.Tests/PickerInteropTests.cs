using System.Runtime.InteropServices;
using ChatArchive.App.Services;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class PickerInteropTests : IDisposable
{
    public PickerInteropTests()
    {
        PickerInterop.HandleValidator = static hwnd => hwnd == 42;
    }

    public void Dispose()
    {
        PickerInterop.HandleValidator = null;
    }

    [Fact]
    public void RequireHandle_rejects_zero()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PickerInterop.RequireHandle(0));
        Assert.Contains("窗口尚未准备好", ex.Message);
    }

    [Fact]
    public void RequireHandle_rejects_nonzero_unusable_handle()
    {
        Assert.Throws<InvalidOperationException>(() => PickerInterop.RequireHandle(7));
    }

    [Fact]
    public void RequireHandle_returns_usable_handle()
    {
        Assert.Equal(42, PickerInterop.RequireHandle(42));
    }

    [Fact]
    public void FormatFailure_includes_com_hresult()
    {
        var com = new COMException("Invalid window handle.", unchecked((int)0x80070578));
        var text = PickerInterop.FormatFailure("更改存储目录", com);
        Assert.Contains("更改存储目录失败", text);
        Assert.Contains("0x80070578", text);
        Assert.Contains("Invalid window handle", text);
    }

    [Fact]
    public void FormatFailure_uses_plain_message_for_other_exceptions()
    {
        var text = PickerInterop.FormatFailure("更改存储目录", new InvalidOperationException("目标目录不能与源目录相同。"));
        Assert.Equal("更改存储目录失败: 目标目录不能与源目录相同。", text);
    }
}
