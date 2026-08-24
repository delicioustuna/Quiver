namespace Yatagarasu.Core;

// readonly struct 実装でジェネリック特殊化による zero-overhead ディスパッチ、
// ref struct 実装でスクラッチバッファ保持が可能。
/// <summary>
/// float 信号スパンに対する二入力演算子。
/// </summary>
/// <remarks>
/// 入力要素型は常に <see cref="float"/> (Yatagarasu ベクトルの格納型)。
/// <typeparamref name="TResult"/> が出力型 (スカラスコア、変換スパン等) を決定する。
/// </remarks>
/// <typeparam name="TResult">演算結果の型。</typeparam>
public interface IDyadicOperator<TResult>
{
    /// <summary>二つの信号スパンから <paramref name="regions"/> の範囲で結果を計算する。</summary>
    /// <param name="a">第 1 入力信号。</param>
    /// <param name="b">第 2 入力信号。</param>
    /// <param name="regions">
    /// 評価対象のサブ範囲。各リージョンの結果の合成方法 (加算、重み付け等) は実装が決定する。
    /// 空スパンは「全範囲」を意味する。
    /// </param>
    TResult Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions);

    /// <summary>二つの信号スパンの全範囲から結果を計算する。</summary>
    TResult Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Invoke(a, b, ReadOnlySpan<Range>.Empty);
}

/// <summary>
/// float 信号スパンに対する単入力演算子。
/// </summary>
/// <typeparam name="TResult">演算結果の型。</typeparam>
public interface IMonadicOperator<TResult>
{
    /// <summary>単一信号スパンから <paramref name="regions"/> の範囲で結果を計算する。</summary>
    TResult Invoke(ReadOnlySpan<float> a, ReadOnlySpan<Range> regions);

    /// <summary>単一信号スパンの全範囲から結果を計算する。</summary>
    TResult Invoke(ReadOnlySpan<float> a)
        => Invoke(a, ReadOnlySpan<Range>.Empty);
}
