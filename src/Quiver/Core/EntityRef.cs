namespace Quiver.Core;

/// <summary>
/// 物理 ID の統一パック表現。旧 <c>GenerationalRef</c> を吸収し、
/// 索引値レーン・ベクトル binding キー・外部往復 ID と、論理 ID 構造体
/// (<see cref="VertexId"/> / <see cref="EdgeId"/> / <see cref="NexusId"/>) の
/// 内部 <c>Value</c> を、ただ一つの packing 規約に集約する。
/// <para>レイアウト (上位→下位):</para>
/// <list type="bullet">
///  <item>bits [63..60] = <see cref="EntityKind"/> (4bit)。<b>cross-kind 物理形</b>
///    (<see cref="Pack"/>) でのみ格納する。論理 ID 構造体の <c>Value</c> は kind を C# 型で
///    表現するため kind ビットを持たない (<see cref="PackLocal"/>)。</item>
///  <item>bits [59..44] = Generation (16bit)。slot ごとの incarnation。65,536 回再利用まで。</item>
///  <item>bits [43..0]  = Sequence (44bit)。slot の局所 ID。17.6 兆まで。</item>
/// </list>
/// <para><see cref="Generation"/> / <see cref="Sequence"/> は下位 60bit だけを見るため、
/// kind ビットの有無 (cross-kind packed / kind 消去ローカル形) を問わず同じ結果を返す。
/// Generation は slot incarnation を表し、MVCC version (xmin/xmax/sstamp) とは別概念。</para>
/// </summary>
public readonly partial record struct EntityRef
{
    private EntityRef(EntityKind kind, long value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>エンティティの種別。</summary>
    public EntityKind Kind { get; }

    /// <summary>種別を含まない Generation + Sequence の packed 値。</summary>
    public long Value { get; }

    /// <summary>有効な Vertex、Edge、または Nexus を表すか。</summary>
    public bool IsValid => IsSupportedKind(Kind) && Value >= 0;

    /// <summary>slot の局所 ID。</summary>
    public long Sequence => IsValid ? UnpackSequence(Value) : -1;

    /// <summary>slot の incarnation。</summary>
    public int Generation => IsValid ? UnpackGeneration(Value) : 0;

    /// <summary>Kind フィールドの開始ビット位置。</summary>
    public const int KindShift = 60;

    /// <summary>Generation フィールドの開始ビット位置。</summary>
    public const int GenerationShift = 44;

    /// <summary>Generation のビット幅。</summary>
    public const int GenerationBits = 16;

    /// <summary>Sequence のビット幅。</summary>
    public const int SequenceBits = 44;

    /// <summary>Sequence の最大値 (= 下位 44 ビットのマスク)。</summary>
    public const long SequenceMask = (1L << SequenceBits) - 1;

    /// <summary>Generation の最大値。これを超える再利用要求があった slot は永久退役させる。</summary>
    public const int MaxGeneration = (1 << GenerationBits) - 1;

    private const long GenerationMask = (1L << GenerationBits) - 1;

    /// <summary>
    /// <b>cross-kind 物理形</b>: (Kind, Sequence, Generation) を 64 ビットへ。索引値レーン /
    /// ベクトル binding キー / 外部往復 ID 用。<paramref name="sequence"/> は
    /// 0..<see cref="SequenceMask"/>、<paramref name="generation"/> は 0..<see cref="MaxGeneration"/>。
    /// </summary>
    public static long Pack(EntityKind kind, long sequence, int generation)
    {
        ValidateKind(kind);
        Validate(sequence, generation);
        return ((long)(byte)kind << KindShift)
             | ((long)generation << GenerationShift)
             | (sequence & SequenceMask);
    }

    /// <summary>
    /// <b>kind 消去ローカル形</b>: (Sequence, Generation) を 64 ビットへ (kind ビット無し)。
    /// 論理 ID 構造体の <c>Value</c> 用。<c>generation == 0</c> のとき結果は <paramref name="sequence"/>
    /// と一致する (= 旧来の「Value == Sequence」後方互換)。
    /// </summary>
    public static long PackLocal(long sequence, int generation)
    {
        Validate(sequence, generation);
        return ((long)generation << GenerationShift) | (sequence & SequenceMask);
    }

    /// <summary>パック済み値から <see cref="EntityKind"/> を取り出す (kind 消去ローカル形では 0)。</summary>
    public static EntityKind UnpackKind(long packed) => (EntityKind)(byte)((ulong)packed >> KindShift & 0xF);

    /// <summary>パック済み値から Generation を取り出す (kind ビット有無を問わない)。</summary>
    public static int UnpackGeneration(long packed) => (int)((ulong)packed >> GenerationShift & GenerationMask);

    /// <summary>パック済み値から Sequence (局所 ID) を取り出す (kind ビット有無を問わない)。</summary>
    public static long UnpackSequence(long packed) => packed & SequenceMask;

    /// <summary><see cref="VertexId"/> から種別付き参照を作成する。</summary>
    public static EntityRef From(VertexId id) => From(EntityKind.Vertex, id.Value);

    /// <summary><see cref="EdgeId"/> から種別付き参照を作成する。</summary>
    public static EntityRef From(EdgeId id) => From(EntityKind.Edge, id.Value);

    /// <summary><see cref="NexusId"/> から種別付き参照を作成する。</summary>
    public static EntityRef From(NexusId id) => From(EntityKind.Nexus, id.Value);

    /// <summary>種別、slot、世代を検証して種別付き参照を作成する。</summary>
    public static EntityRef Create(EntityKind kind, long sequence, int generation)
    {
        ValidateKind(kind);
        return new EntityRef(kind, PackLocal(sequence, generation));
    }

    private static EntityRef From(EntityKind kind, long value)
    {
        ValidateKind(kind);
        if (value == -1) return default;
        ValidateLocal(value);
        return new EntityRef(kind, value);
    }

    private static void Validate(long sequence, int generation)
    {
        if (sequence < 0 || sequence > SequenceMask)
            throw new ArgumentOutOfRangeException(nameof(sequence),
                $"Sequence {sequence} は {SequenceBits} ビット (0..{SequenceMask}) に収まりません。");
        if (generation < 0 || generation > MaxGeneration)
            throw new ArgumentOutOfRangeException(nameof(generation),
                $"Generation {generation} は {GenerationBits} ビット (0..{MaxGeneration}) に収まりません。");
    }

    private static void ValidateKind(EntityKind kind)
    {
        if (!IsSupportedKind(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "EntityRef は Vertex、Edge、Nexus だけを受け入れます。");
    }

    private static bool IsSupportedKind(EntityKind kind)
        => kind is EntityKind.Vertex or EntityKind.Edge or EntityKind.Nexus;

    private static void ValidateLocal(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "EntityRef の local value は非負でなければなりません。");
        if ((ulong)value >> KindShift != 0)
            throw new ArgumentOutOfRangeException(nameof(value), "EntityRef の local value に kind bit を含めることはできません。");
    }
}
