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
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("12_")]
    [InlineData("_34")]
    [InlineData("1_2_3")]
    public void Decode_rejects_invalid(string? cursor)
    {
        Assert.Throws<FormatException>(() => Data.CursorCodec.Decode(cursor!));
        Assert.False(Data.CursorCodec.TryDecode(cursor, out _, out _));
    }

    [Theory]
    [InlineData("1700000000000_42", 1700000000000, 42)]
    public void TryDecode_succeeds_for_valid(string cursor, long expectedTs, long expectedId)
    {
        Assert.True(Data.CursorCodec.TryDecode(cursor, out var ts, out var id));
        Assert.Equal(expectedTs, ts);
        Assert.Equal(expectedId, id);
    }
}
