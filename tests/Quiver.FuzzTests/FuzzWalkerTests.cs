using FluentAssertions;
using Quiver.FuzzTests.Targets;
using Xunit;

namespace Quiver.FuzzTests;

/// <summary>
/// fuzz target を inline seed + filesystem corpus + regression 入力で総当たり実行する。
/// `[Trait("Category", "Fuzz")]` で日常 run から外し、`dotnet test --filter Category=Fuzz`
/// または CI nightly で回す。
///
/// 各 target は「いかなる入力に対しても例外を投げず終わる」が契約。テストは
/// <see cref="Record.Exception"/> で実際の例外有無を確認し、無であれば緑。
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

    // ---- WAL レコード / segment ファイル --------------------------------------

    public static IEnumerable<object[]> WalRecordInputs()
    {
        for (int i = 0; i < InlineSeeds.WalRecord.Length; i++)
            yield return new object[] { $"inline-{i:D2}", InlineSeeds.WalRecord[i] };
        foreach (var row in FuzzCorpus.Enumerate("wal-record"))
            yield return row;
    }

    [Theory]
    [MemberData(nameof(WalRecordInputs))]
    public void WalRecord_does_not_throw(string label, byte[] input)
    {
        _ = label;
        var ex = Record.Exception(() => WalRecordFuzzTarget.Run(input));
        ex.Should().BeNull("WriteAheadLog の RebuildState / OpenReader は破損 WAL を吸収すべきで、例外を漏らしてはならない");
    }
}
