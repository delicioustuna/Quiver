using Xunit;
using GraphDb.Engine.Wal;
using FluentAssertions;

namespace GraphDb.Engine.Wal.Tests;

public class WalTests
{
    [Fact]
    public void WalRecordType_Values_AreCorrect()
    {
        ((byte)WalRecordType.Begin).Should().Be(1);
        ((byte)WalRecordType.Commit).Should().Be(2);
        ((byte)WalRecordType.EndOfSegment).Should().Be(0xFE);
    }
}
