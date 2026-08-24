using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.FuzzTests.Targets;
using Xunit;

namespace Yatagarasu.FuzzTests;

/// <summary>
/// fuzz target を inline seed + filesystem corpus + regression 入力で総当たり実行する。
/// `[Trait("Category", "Fuzz")]` で日常 run から外し、`dotnet test --filter Category=Fuzz`
/// または CI nightly で回す。
///
/// codec target は任意入力を安全に拒否する。
/// WAL file target は strict parser の定義済み format/corruption 例外を許容し、それ以外の例外を検出する。
/// </summary>
[Trait("Category", "Fuzz")]
public class FuzzWalkerTests
{
    // ---- WAL page image codec --------------------------------------------------

    public static IEnumerable<object[]> WalPageImageInputs()
    {
        for (int i = 0; i < InlineSeeds.WalPageImage.Length; i++)
            yield return new object[] { $"inline-{i:D2}", InlineSeeds.WalPageImage[i] };
        foreach (var row in FuzzCorpus.Enumerate("wal-pageimage"))
            yield return row;
    }

    [Theory]
    [MemberData(nameof(WalPageImageInputs))]
    public void WalPageImageCodec_does_not_throw(string label, byte[] input)
    {
        _ = label;
        var ex = Record.Exception(() => WalPageImageCodecFuzzTarget.Run(input));
        ex.Should().BeNull("WalPageImageCodec.TryDecode は任意入力で false を返すだけで例外を投げてはならない");
    }

    // ---- SpanCodec varint ------------------------------------------------------

    public static IEnumerable<object[]> SpanCodecInputs()
    {
        for (int i = 0; i < InlineSeeds.SpanCodec.Length; i++)
            yield return new object[] { $"inline-{i:D2}", InlineSeeds.SpanCodec[i] };
        foreach (var row in FuzzCorpus.Enumerate("spancodec"))
            yield return row;
    }

    [Theory]
    [MemberData(nameof(SpanCodecInputs))]
    public void SpanCodec_TryReadVarInt64_does_not_throw(string label, byte[] input)
    {
        _ = label;
        var ex = Record.Exception(() => SpanCodecFuzzTarget.Run(input));
        ex.Should().BeNull("TryReadVarInt64 は truncated / 過長で false を返すだけで例外を投げてはならない");
    }

    // ---- WAL レコードファイル -------------------------------------------------

    public static IEnumerable<object[]> WalRecordInputs()
    {
        for (int i = 0; i < InlineSeeds.WalRecord.Length; i++)
            yield return new object[] { $"inline-{i:D2}", InlineSeeds.WalRecord[i] };
        foreach (var row in FuzzCorpus.Enumerate("wal-record"))
            yield return row;
    }

    [Theory]
    [MemberData(nameof(WalRecordInputs))]
    public void WalRecord_rejects_invalid_input_with_defined_exception(string label, byte[] input)
    {
        _ = label;
        var ex = Record.Exception(() => WalRecordFuzzTarget.Run(input));
        bool isDefinedOutcome = ex is null
            || ex.GetType() == typeof(WalFormatMismatchException)
            || ex.GetType() == typeof(CorruptionException);
        isDefinedOutcome.Should().BeTrue(
            "strict WAL parser は入力を受理するか、format/corruption 例外で明示拒否する");
    }
}
