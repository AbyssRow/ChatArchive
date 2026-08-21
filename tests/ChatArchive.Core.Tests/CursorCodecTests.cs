using Xunit;

namespace ChatArchive.Core.Tests;

public class CursorCodecTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1700000000000, 42)]
    [InlineData(long.MaxValue, long.MaxValue)]
    [InlineData(-1, 1)]
    public void RoundTrip_preserves_values(long timestampMs, long id)
    {
        var cursor = Data.CursorCodec.Encode(timestampMs, id);
        var decoded = Data.CursorCodec.Decode(cursor);
        Assert.Equal(timestampMs, decoded.TimestampMs);
        Assert.Equal(id, decoded.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12_")]
    [InlineData("_34")]
    [InlineData("1_2_3")]
    public void Decode_rejects_invalid(string cursor)
    {
        Assert.Throws<FormatException>(() => Data.CursorCodec.Decode(cursor));
    }
}
