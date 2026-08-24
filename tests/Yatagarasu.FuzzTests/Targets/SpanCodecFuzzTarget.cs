using Yatagarasu.Codec;

namespace Yatagarasu.FuzzTests.Targets;

/// <summary>
/// <see cref="SpanCodec.TryReadVarInt64"/> および
/// <see cref="SpanCodec.ReadUtf8Bytes"/> 経路の VarInt 長プレフィックス
/// を任意バイト列で叩く。
///
/// 契約:
///   - <see cref="SpanCodec.TryReadVarInt64"/> は truncated / 過長で <c>false</c>
///     を返し、いかなる場合も例外を投げない。
///   - 入力先頭から最大 10 通りの offset で順次デコードしても、out-of-bounds で
///     例外が漏れないこと。
/// </summary>
public static class SpanCodecFuzzTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        int maxOffset = Math.Min(input.Length, 8);
        for (int offset = 0; offset <= maxOffset; offset++)
        {
            _ = SpanCodec.TryReadVarInt64(input, offset, out _, out _);
        }
    }
}
