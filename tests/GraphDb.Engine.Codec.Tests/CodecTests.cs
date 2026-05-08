using Xunit;
using GraphDb.Engine.Codec;
using FluentAssertions;

namespace GraphDb.Engine.Codec.Tests;

public class CodecTests
{
    [Fact]
    public void ReadWriteInt32_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[4];
        SpanCodec.WriteInt32(buf, 0, 42);
        SpanCodec.ReadInt32(buf, 0).Should().Be(42);
    }

    [Fact]
    public void ReadWriteInt64_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[8];
        SpanCodec.WriteInt64(buf, 0, long.MaxValue);
        SpanCodec.ReadInt64(buf, 0).Should().Be(long.MaxValue);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void VarInt_RoundTrip(long value)
    {
        Span<byte> buf = stackalloc byte[10];
        int written = SpanCodec.WriteVarInt64(buf, 0, value);
        long decoded = SpanCodec.ReadVarInt64(buf, 0, out int consumed);
        decoded.Should().Be(value);
        consumed.Should().Be(written);
    }
}
