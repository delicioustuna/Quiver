using Yatagarasu.Storage.Wal;

namespace Yatagarasu.FuzzTests.Targets;

/// <summary>
/// <see cref="WalPageImageCodec.TryDecode"/> を任意バイト列で叩く。
/// 契約: いかなる入力に対しても <c>false</c> を返すか正常 decode するだけで、
/// 例外で抜けてはならない (v1/v2/v3 header / chunk varint / usedLen の境界)。
/// </summary>
public static class WalPageImageCodecFuzzTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        _ = WalPageImageCodec.TryDecode(input, out _, out _, out _);
    }
}
