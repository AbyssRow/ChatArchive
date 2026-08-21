using ChatArchive.App.ViewModels;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class UiInputTests
{
    [Theory]
    [InlineData("", null, null)]
    [InlineData("qq|private", "qq", "private")]
    [InlineData("wechat|group", "wechat", "group")]
    [InlineData("qq", null, null)]
    [InlineData("qq|group|extra", null, null)]
    public void Conversation_filter_tag_is_parsed_without_index_errors(
        string tag,
        string? expectedPlatform,
        string? expectedKind)
    {
        var result = UiInputParser.ParseConversationFilter(tag);

        Assert.Equal(expectedPlatform, result.Platform);
        Assert.Equal(expectedKind, result.Kind);
    }

    [Theory]
    [InlineData(@"E:\media\photo.jpg", ".jpg")]
    [InlineData(@"E:\media\image", ".png")]
    [InlineData(@"E:\media\photo.", ".png")]
    public void Picker_extension_always_starts_with_dot(string path, string expected)
    {
        Assert.Equal(expected, UiInputParser.PickerExtension(path));
    }

    [Fact]
    public void Latest_request_gate_rejects_older_versions()
    {
        var gate = new LatestRequestGate();
        var first = gate.Next();
        var second = gate.Next();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }
}
