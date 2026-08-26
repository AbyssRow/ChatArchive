using ChatArchive.App.Views;
using Microsoft.UI.Xaml;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class ConverterTests
{
    [Fact]
    public void PathToImageSource_returns_null_for_empty_or_nonexistent_paths()
    {
        var converter = new PathToImageSourceConverter();

        Assert.Null(converter.Convert(null, typeof(object), null, string.Empty));
        Assert.Null(converter.Convert(string.Empty, typeof(object), null, string.Empty));
        Assert.Null(converter.Convert("   ", typeof(object), null, string.Empty));
        Assert.Null(converter.Convert("non_existent_file_path_12345.png", typeof(object), null, string.Empty));
        Assert.Null(converter.Convert(12345, typeof(object), null, string.Empty));
    }

    [Fact]
    public void PathToImageSource_convert_back_throws()
    {
        var converter = new PathToImageSourceConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(null!, typeof(object), null!, string.Empty));
    }

    [Fact]
    public void PlatformLabel_converts_known_platforms()
    {
        var converter = new PlatformLabelConverter();
        Assert.Equal("QQ", converter.Convert("qq", typeof(string), null!, string.Empty));
        Assert.Equal("微信", converter.Convert("wechat", typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert("other", typeof(string), null!, string.Empty));
    }

    [Fact]
    public void CountText_converts_numbers()
    {
        var converter = new CountTextConverter();
        Assert.Equal("1,234 条", converter.Convert(1234L, typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert("not long", typeof(string), null!, string.Empty));
    }
}
