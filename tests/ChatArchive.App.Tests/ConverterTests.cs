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
    public void PlatformLabel_converts_known_platforms_and_falls_back_to_original()
    {
        var converter = new PlatformLabelConverter();
        Assert.Equal("QQ", converter.Convert("qq", typeof(string), null!, string.Empty));
        Assert.Equal("微信", converter.Convert("wechat", typeof(string), null!, string.Empty));
        Assert.Equal("文本/MD", converter.Convert("text", typeof(string), null!, string.Empty));
        Assert.Equal("网页", converter.Convert("html", typeof(string), null!, string.Empty));
        Assert.Equal("SQL", converter.Convert("sql", typeof(string), null!, string.Empty));
        Assert.Equal("telegram", converter.Convert("telegram", typeof(string), null!, string.Empty));
        Assert.Equal("other", converter.Convert("other", typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert(null!, typeof(string), null!, string.Empty));
    }

    [Fact]
    public void CountText_converts_numbers()
    {
        var converter = new CountTextConverter();
        Assert.Equal("1,234 条", converter.Convert(1234L, typeof(string), null!, string.Empty));
        Assert.Equal("567 条", converter.Convert(567, typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert("not long", typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert(null!, typeof(string), null!, string.Empty));
    }

    [Fact]
    public void MsToDateTime_converts_valid_ms_and_safely_handles_out_of_bounds()
    {
        var converter = new MsToDateTimeConverter();
        Assert.Equal(string.Empty, converter.Convert(-100L, typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert(long.MaxValue, typeof(string), null!, string.Empty));
        Assert.Equal(string.Empty, converter.Convert("not a long", typeof(string), null!, string.Empty));

        var valid = converter.Convert(1700000000000L, typeof(string), null!, string.Empty);
        Assert.False(string.IsNullOrWhiteSpace(valid as string));
    }
}
