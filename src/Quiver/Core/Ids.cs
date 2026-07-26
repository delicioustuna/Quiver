namespace Quiver.Core;

// VertexId / EdgeId / NexusId の Value は packed 物理 ID
// (kind 消去ローカル形 Gen16<<44 | Seq44、kind は型で表現)。
//  - Sequence (slot 局所 ID) は record の page/offset 演算に使う。オンディスクの ID
//    フィールド (Int48) には Value ではなく Sequence を書く。
//  - Generation は slot incarnation。Allocate/Read が sidecar 由来の世代を載せて払い出す。
//    Generation 0 (= new XId(seq)) はストア内の物理 Sequence 表現にだけ使う。
// Why not Sequence-only equality: vacuum 後の slot 再利用で別 entity を同一キーとして扱い、
// dictionary、frontier、index key が stale reference を現在の entity へ alias してしまう。

/// <summary>Vertexの識別子。<paramref name="Value"/> は世代 (上位) と slot 局所 ID (下位) を詰めた packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct VertexId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly VertexId Invalid = new(-1);

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

    /// <summary>(sequence, generation) から packed な <see cref="VertexId"/> を生成する。</summary>
    public static VertexId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>Generation を含む packed identity が等しいかを判定する。</summary>
    public bool Equals(VertexId other) => Value == other.Value;

    /// <summary>Generation を含む packed identity のハッシュ値。</summary>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Edge (エッジ) の識別子。<paramref name="Value"/> は世代 + slot 局所 ID の packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct EdgeId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly EdgeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.UnpackSequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    /// <summary>(sequence, generation) から packed な <see cref="EdgeId"/> を生成する。</summary>
    public static EdgeId Create(long sequence, int generation) => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>Generation を含む packed identity が等しいかを判定する。</summary>
    public bool Equals(EdgeId other) => Value == other.Value;

    /// <summary>Generation を含む packed identity のハッシュ値。</summary>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Nexusの識別子。<paramref name="Value"/> は世代 + slot 局所 ID の packed 値。</summary>
/// <param name="Value">packed 物理 ID (Generation &lt;&lt; 44 | Sequence)。</param>
public readonly record struct NexusId(long Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly NexusId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;

    /// <summary>slot 局所 ID (下位 44bit)。負値 (Invalid) は sentinel をそのまま返す。</summary>
    public long Sequence => Value < 0 ? Value : EntityRef.UnpackSequence(Value);

    /// <summary>slot incarnation (bits 44-59)。世代未指定 (= new(seq)) は 0。</summary>
    public int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    /// <summary>(sequence, generation) から packed な <see cref="NexusId"/> を生成する。</summary>
    public static NexusId Create(long sequence, int generation)
        => new(EntityRef.PackLocal(sequence, generation));

    /// <summary>Generation を含む packed identity が等しいかを判定する。</summary>
    public bool Equals(NexusId other) => Value == other.Value;

    /// <summary>Generation を含む packed identity のハッシュ値。</summary>
    public override int GetHashCode() => Value.GetHashCode();
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

/// <summary>Edge型の識別子 (トークンストアが払い出す稠密 int)。</summary>
/// <param name="Value">Edge型トークン ID。</param>
public readonly record struct EdgeTypeId(int Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly EdgeTypeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}

/// <summary>Nexus型の識別子 (トークンストアが払い出す稠密 int)。</summary>
/// <param name="Value">Nexus型トークン ID。</param>
public readonly record struct NexusTypeId(int Value)
{
    /// <summary>無効値を表す sentinel (<see cref="Value"/> = -1)。</summary>
    public static readonly NexusTypeId Invalid = new(-1);

    /// <summary>有効な ID か (<see cref="Value"/> が非負か)。</summary>
    public bool IsValid => Value >= 0;
}

/// <summary>Nexus内のロールを表す内部トークン ID。</summary>
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
