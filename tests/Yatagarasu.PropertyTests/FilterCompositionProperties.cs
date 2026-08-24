using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.PropertyTests;

/// <summary>
/// <see cref="FilterOperator"/> 合成の関係代数上の恒等性を検証する。
///
/// 検証する不変量 (関係代数の filter 公理):
///   F1. <c>σ_p ∘ σ_q (R) ≡ σ_q ∘ σ_p (R)</c>  (filter 合成は可換)
///   F2. <c>σ_p ∘ σ_q (R) ≡ σ_{p ∧ q} (R)</c>  (連続 filter は AND と等価)
///   F3. <c>σ_p (σ_p (R)) ≡ σ_p (R)</c>          (filter は冪等)
///   F4. <c>σ_p (R)</c> は <c>R</c> の (順序を保った) 部分シーケンス
/// </summary>
public class FilterCompositionProperties
{
    /// <summary>テスト用述語: 指定パターンに従って tuple[0].LongValue を判定する。</summary>
    private sealed class PredicateMod : IPredicate
    {
        private readonly int _mod;
        private readonly int _remainder;
        public PredicateMod(int mod, int remainder) { _mod = mod; _remainder = remainder; }
        public bool Evaluate(in TupleRef tuple, ITransaction tx)
            => ((int)(tuple[0].LongValue % _mod) + _mod) % _mod == _remainder;
    }

    private sealed class PredicateLessThan : IPredicate
    {
        private readonly long _threshold;
        public PredicateLessThan(long threshold) { _threshold = threshold; }
        public bool Evaluate(in TupleRef tuple, ITransaction tx) => tuple[0].LongValue < _threshold;
    }

    private sealed class PredicateGreaterThan : IPredicate
    {
        private readonly long _threshold;
        public PredicateGreaterThan(long threshold) { _threshold = threshold; }
        public bool Evaluate(in TupleRef tuple, ITransaction tx) => tuple[0].LongValue > _threshold;
    }

    private sealed class AndPredicate : IPredicate
    {
        private readonly IPredicate _a;
        private readonly IPredicate _b;
        public AndPredicate(IPredicate a, IPredicate b) { _a = a; _b = b; }
        public bool Evaluate(in TupleRef tuple, ITransaction tx)
            => _a.Evaluate(in tuple, tx) && _b.Evaluate(in tuple, tx);
    }

    /// <summary>
    /// 単一カラム (LongValue) を返すソース演算子。FilterOperator のテスト専用。
    /// FixedVertexListOperator が Support 配下の internal にあるため、ここで類似のものを定義する。
    /// </summary>
    private sealed class LongListOperator : IPhysicalOperator
    {
        private readonly long[] _items;
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];

        public LongListOperator(long[] items) => _items = items;

        public TupleSchema Schema { get; } = new([new ColumnDefinition("v", TupleSlotType.Int64)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);

        public void Open(ITransaction tx) { _index = -1; }

        public bool MoveNext()
        {
            if (++_index >= _items.Length) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.Int64, LongValue = _items[_index] };
            return true;
        }

        public void Dispose() { }
    }

    private static List<long> Run(IPhysicalOperator op)
    {
        var result = new List<long>();
        op.Open(null!);
        while (op.MoveNext()) result.Add(op.Current[0].LongValue);
        op.Dispose();
        return result;
    }

    public sealed record FilterCase(long[] Items, int Mod, int Remainder, long Threshold);

    public static Arbitrary<FilterCase> Arb()
    {
        var itemsGen = Gen.Sized(s =>
            ArbMap.Default.GeneratorFor<long>()
                .Select(v => v % 1000) // -999..999 のレンジに絞る
                .ListOf(Math.Min(s, 32))
                .Select(l => l.ToArray()));
        var modGen = Gen.Choose(2, 7);
        var remGen = Gen.Choose(0, 6);
        var thresholdGen = ArbMap.Default.GeneratorFor<long>().Select(v => v % 500);
        return itemsGen
            .SelectMany(items => modGen
                .SelectMany(mod => remGen
                    .SelectMany(rem => thresholdGen
                        .Select(t => new FilterCase(items, mod, rem % mod, t)))))
            .ToArbitrary();
    }

    [Property(MaxTest = 200, Arbitrary = [typeof(FilterCompositionProperties)])]
    public Property F1_FilterComposition_IsCommutative(FilterCase c)
    {
        var p1 = new PredicateMod(c.Mod, c.Remainder);
        var p2 = new PredicateGreaterThan(c.Threshold);

        var ab = Run(new FilterOperator(new FilterOperator(new LongListOperator(c.Items), p1), p2));
        var ba = Run(new FilterOperator(new FilterOperator(new LongListOperator(c.Items), p2), p1));

        return ab.SequenceEqual(ba).ToProperty();
    }

    [Property(MaxTest = 200, Arbitrary = [typeof(FilterCompositionProperties)])]
    public Property F2_ConsecutiveFilters_EqualToConjunction(FilterCase c)
    {
        var p1 = new PredicateMod(c.Mod, c.Remainder);
        var p2 = new PredicateLessThan(c.Threshold);

        var chained = Run(new FilterOperator(new FilterOperator(new LongListOperator(c.Items), p1), p2));
        var combined = Run(new FilterOperator(new LongListOperator(c.Items), new AndPredicate(p1, p2)));

        return chained.SequenceEqual(combined).ToProperty();
    }

    [Property(MaxTest = 200, Arbitrary = [typeof(FilterCompositionProperties)])]
    public Property F3_Filter_IsIdempotent(FilterCase c)
    {
        var p = new PredicateMod(c.Mod, c.Remainder);

        var once = Run(new FilterOperator(new LongListOperator(c.Items), p));
        var twice = Run(new FilterOperator(new FilterOperator(new LongListOperator(c.Items), p), p));

        return once.SequenceEqual(twice).ToProperty();
    }

    [Property(MaxTest = 200, Arbitrary = [typeof(FilterCompositionProperties)])]
    public Property F4_Filter_PreservesOrderAndIsSubsequence(FilterCase c)
    {
        var p = new PredicateGreaterThan(c.Threshold);
        var filtered = Run(new FilterOperator(new LongListOperator(c.Items), p));

        // 出現順は ソースと同じ。filtered を ソース上で先頭から喰っていけば全部マッチする。
        int j = 0;
        foreach (var v in c.Items)
        {
            if (j < filtered.Count && v == filtered[j]) j++;
        }
        return (j == filtered.Count).ToProperty();
    }
}
