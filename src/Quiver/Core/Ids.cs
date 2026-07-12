namespace Quiver.Core;

// NodeId / RelationshipId / PropertyId の Value は packed 物理 ID
// (kind 消去ローカル形 Gen16<<44 | Seq44、kind は型で表現)。
//  - Sequence (slot 局所 ID) は record の page/offset 演算に使う。オンディスクの ID
//    フィールド (Int48) には Value ではなく Sequence を書く。
//  - Generation は slot incarnation。Allocate/Read が sidecar 由来の世代を載せて払い出す。
//    生成 0 (= new XId(seq)) は「世代未指定」で、Value == Sequence の後方互換。
// Why not Sequence-only equality: vacuum 後の slot 再利用で別 entity を同一キーとして扱い、
// dictionary、frontier、index key が stale reference を現在の entity へ alias してしまう。

/// <summary>ノードの識別子。<paramref name="Value"/> は世代 (上位) と slot 局所 ID (下位) を詰めた packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct NodeId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly NodeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// slot 局所 ID (下位 44bit)。page/offset 演算とオンディスク Int48 格納に使う。
    /// 負値 (Invalid = -1) は sentinel をそのまま返す (= Int48 に書くと -1 で復元され、
    /// chain 終端 / 無効リンクが保たれる)。
    /// </summary>
    public long Sequence => Value < 0 ? Value : EntityRef.UnpackSequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    /// <summary>(sequence, generation) から packed な <see cref="NodeId"/> を生成する。</summary>
    public static NodeId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>Generation を含む packed identity が等しいかを判定する。</summary>
    public bool Equals(NodeId other) => Value == other.Value;

    /// <summary>Generation を含む packed identity のハッシュ値。</summary>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>リレーションシップ (エッジ) の識別子。<paramref name="Value"/> は世代 + slot 局所 ID の packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct RelationshipId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly RelationshipId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.UnpackSequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    /// <summary>(sequence, generation) から packed な <see cref="RelationshipId"/> を生成する。</summary>
    public static RelationshipId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>Generation を含む packed identity が等しいかを判定する。</summary>
    public bool Equals(RelationshipId other) => Value == other.Value;

    /// <summary>Generation を含む packed identity のハッシュ値。</summary>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>ハイパーエッジの識別子。<paramref name="Value"/> は世代 + slot 局所 ID の packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct HyperedgeId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly HyperedgeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.UnpackSequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    /// <summary>(sequence, generation) から packed な <see cref="HyperedgeId"/> を生成する。</summary>
    public static HyperedgeId Create(long sequence, int generation)
        => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>Generation を含む packed identity が等しいかを判定する。</summary>
    public bool Equals(HyperedgeId other) => Value == other.Value;

    /// <summary>Generation を含む packed identity のハッシュ値。</summary>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>プロパティレコードの識別子。<paramref name="Value"/> は世代 + slot 局所 ID の packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct PropertyId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly PropertyId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.UnpackSequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    /// <summary>(sequence, generation) から packed な <see cref="PropertyId"/> を生成する。</summary>
    public static PropertyId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>slot (Sequence) ベースで同一プロパティかを判定する (世代差は無視)。</summary>
    public bool Equals(PropertyId other) => Sequence == other.Sequence;

    /// <summary>Sequence ベースのハッシュ値 (<see cref="Equals(PropertyId)"/> と整合)。</summary>
    public override int GetHashCode() => Sequence.GetHashCode();
}

/// <summary>ラベルの識別子 (トークンストアが払い出す稠密 int)。</summary>
/// <param name="Value">ラベルトークン ID。</param>
public readonly record struct LabelId(int Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly LabelId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}

/// <summary>リレーションシップ型の識別子 (トークンストアが払い出す稠密 int)。</summary>
/// <param name="Value">リレーションシップ型トークン ID。</param>
public readonly record struct RelationshipTypeId(int Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly RelationshipTypeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}

/// <summary>ハイパーエッジ型の識別子 (トークンストアが払い出す稠密 int)。</summary>
/// <param name="Value">ハイパーエッジ型トークン ID。</param>
public readonly record struct HyperedgeTypeId(int Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly HyperedgeTypeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}

/// <summary>ハイパーエッジ内のロールを表す内部トークン ID。</summary>
internal readonly record struct RoleId(int Value)
{
    public static readonly RoleId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

/// <summary>incidence レコードの内部 sequence ID。</summary>
internal readonly record struct IncidenceId(long Value)
{
    public static readonly IncidenceId Invalid = new(-1);
    public bool IsValid => Value >= 0;
    public long Sequence => Value;
}

/// <summary>プロパティキーの識別子 (トークンストアが払い出す稠密 int)。</summary>
/// <param name="Value">プロパティキートークン ID。</param>
public readonly record struct PropertyKeyId(int Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly PropertyKeyId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}

internal readonly record struct PageId(long Value)
{
    public static readonly PageId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

/// <summary>
/// プロパティキーの多重度。<see cref="Single"/> (既定) は 1 キー = 1 値、
/// <see cref="Set"/> は 1 キー = N 値 (重複なし・順序なし)。
/// </summary>
public enum PropertyCardinality : byte
{
    /// <summary>1 キー = 1 値 (既定、現行動作)。</summary>
    Single = 0,
    /// <summary>1 キー = N 値、重複なし、順序なし。</summary>
    Set = 1,
}

/// <summary>トランザクションの識別子 (単調増加する long)。</summary>
/// <param name="Value">トランザクション ID。</param>
public readonly record struct TransactionId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly TransactionId Invalid = new(-1);

    /// <summary>
    /// MVCC コンテキスト未設定時 (bulk loader / recovery / 一部テスト) で xmin に書く既定値。
    /// 起動時に <c>CommittedTxRegistry</c> へ committed として登録され、全 snapshot から可視として扱われる。
    /// </summary>
    public static readonly TransactionId Bootstrap = new(1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}
