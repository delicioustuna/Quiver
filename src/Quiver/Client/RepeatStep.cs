using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// <c>.Repeat(s =&gt; s.Out("KNOWS")).Times(n)</c> 用の fluent な記述レコーダ。
/// 単一ステップの展開 (<see cref="Out"/> / <see cref="In"/> /
/// <see cref="Both"/> のいずれか 1 つ) のみを記録し、最後の呼び出しが採用される。
/// クロージャ内のフィルタや連鎖展開は未対応 — 内部の <c>VariableLengthExpandOperator</c> が
/// 1 つの方向 + 型のみを受け取るため。
/// </summary>
internal sealed class RepeatStep
{
    internal Direction Direction { get; private set; } = Direction.Outgoing;
    internal string? TypeFilter { get; private set; }

    /// <summary>外向 (Outgoing) 単一ホップ。</summary>
    public RepeatStep Out(string? type = null)
    {
        Direction = Direction.Outgoing;
        TypeFilter = type;
        return this;
    }

    /// <summary>内向 (Incoming) 単一ホップ。</summary>
    public RepeatStep In(string? type = null)
    {
        Direction = Direction.Incoming;
        TypeFilter = type;
        return this;
    }

    /// <summary>双方向の単一ホップ。</summary>
    public RepeatStep Both(string? type = null)
    {
        Direction = Direction.Both;
        TypeFilter = type;
        return this;
    }
}
