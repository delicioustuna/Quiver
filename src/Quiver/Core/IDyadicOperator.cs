namespace Quiver.Core;

/// <summary>
/// Two-input operator over float signal spans. The input element type is always
/// <see cref="float"/> (the storage type of Quiver vectors); <typeparamref name="TResult"/>
/// determines the output type (scalar score, transformed span, etc.).
/// <para>
/// Implementations may be <see langword="readonly"/> <see langword="struct"/> for zero-overhead
/// dispatch via generic specialization, or <see langword="ref"/> <see langword="struct"/> when
/// scratch buffers (<see cref="Span{T}"/> fields) are required.
/// </para>
/// </summary>
/// <typeparam name="TResult">Computed result type.</typeparam>
public interface IDyadicOperator<TResult>
{
    /// <summary>Computes a result from two signal spans, restricted to <paramref name="regions"/>.</summary>
    /// <param name="a">First input signal.</param>
    /// <param name="b">Second input signal.</param>
    /// <param name="regions">
    /// Sub-ranges of the input spans to evaluate. The operator decides how to combine
    /// per-region results (additive, weighted, etc.). An empty span means "full extent".
    /// </param>
    TResult Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions);

    /// <summary>Computes a result from two signal spans over their full extent.</summary>
    TResult Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Invoke(a, b, ReadOnlySpan<Range>.Empty);
}

/// <summary>
/// Single-input operator over a float signal span.
/// </summary>
/// <typeparam name="TResult">Computed result type.</typeparam>
public interface IMonadicOperator<TResult>
{
    /// <summary>Computes a result from a single signal span, restricted to <paramref name="regions"/>.</summary>
    TResult Invoke(ReadOnlySpan<float> a, ReadOnlySpan<Range> regions);

    /// <summary>Computes a result from a single signal span over its full extent.</summary>
    TResult Invoke(ReadOnlySpan<float> a)
        => Invoke(a, ReadOnlySpan<Range>.Empty);
}
