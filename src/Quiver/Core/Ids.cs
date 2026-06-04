namespace Quiver.Core;

// ARCH-5b: NodeId / RelationshipId / PropertyId の Value を packed 物理 ID
// (kind 消去ローカル形 Gen16<<44 | Seq44、kind は型で表現) にする。
//   - Sequence (slot 局所 ID) は record の page/offset 演算に使う。オンディスクの ID
//     フィールド (Int48) には Value ではなく Sequence を書く。
//   - Generation は slot incarnation。Allocate/Read が sidecar 由来の世代を載せて払い出す。
//     生成 0 (= new XId(seq)) は「世代未指定」で、Value == Sequence の後方互換。
//   - 同一性 (Equals/GetHashCode) は Sequence ベース: adjacency 由来の gen=0 ID と
//     Read 由来の gen≥1 ID が「同一ノード」として一致し traversal / frontier / dict が壊れない。
//     stale 参照検出は equality ではなく Read の明示世代照合 (TryResolve) で行う。

public readonly record struct NodeId(long Value)
{
    public static readonly NodeId Invalid = new(-1);
    public bool IsValid => Value >= 0;

    /// <summary>
    /// slot 局所 ID (下位 44bit)。page/offset 演算とオンディスク Int48 格納に使う。
    /// 負値 (Invalid = -1) は sentinel をそのまま返す (= Int48 に書くと -1 で復元され、
    /// chain 終端 / 無効リンクが保たれる)。
    /// </summary>
    public long Sequence => Value < 0 ? Value : EntityRef.Sequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.Generation(Value);

    /// <summary>(sequence, generation) から packed な <see cref="NodeId"/> を生成する。</summary>
    public static NodeId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    // ARCH-5b: 同一性は Sequence (slot) ベース。adjacency 由来 (gen=0) と Read 由来 (gen≥1) の
    // 同一ノードを等値とし traversal / frontier / dict を壊さない。stale 検出は TryResolve で行う。
    public bool Equals(NodeId other) => Sequence == other.Sequence;
    public override int GetHashCode() => Sequence.GetHashCode();
}

public readonly record struct RelationshipId(long Value)
{
    public static readonly RelationshipId Invalid = new(-1);
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.Sequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.Generation(Value);

    /// <summary>(sequence, generation) から packed な <see cref="RelationshipId"/> を生成する。</summary>
    public static RelationshipId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    public bool Equals(RelationshipId other) => Sequence == other.Sequence;
    public override int GetHashCode() => Sequence.GetHashCode();
}

public readonly record struct PropertyId(long Value)
{
    public static readonly PropertyId Invalid = new(-1);
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.Sequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.Generation(Value);

    /// <summary>(sequence, generation) から packed な <see cref="PropertyId"/> を生成する。</summary>
    public static PropertyId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    public bool Equals(PropertyId other) => Sequence == other.Sequence;
    public override int GetHashCode() => Sequence.GetHashCode();
}

public readonly record struct LabelId(int Value)
{
    public static readonly LabelId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct RelationshipTypeId(int Value)
{
    public static readonly RelationshipTypeId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct PropertyKeyId(int Value)
{
    public static readonly PropertyKeyId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

internal readonly record struct PageId(long Value)
{
    public static readonly PageId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct TransactionId(long Value)
{
    public static readonly TransactionId Invalid = new(-1);
    /// <summary>
    /// FT-26: MVCC コンテキスト未設定時 (bulk loader / recovery / 一部テスト) で xmin に書く既定値。
    /// 起動時に <c>CommittedTxRegistry</c> へ committed として登録され、全 snapshot から可視として扱われる。
    /// </summary>
    public static readonly TransactionId Bootstrap = new(1);
    public bool IsValid => Value >= 0;
}
