namespace Quiver.Stores;

/// <summary>
/// FT-31: MVCC + SSN (Wang et al. DaMoN'15) のメタデータを sidecar PagedFile に格納するための
/// 1 エントリ。EntityId.LocalId をキーとして <see cref="IEntityVersionStore"/> から read / write される。
///
/// <para>フィールド (32 バイト固定、リトルエンディアン):</para>
/// <list type="bullet">
///   <item><c>Xmin</c> (8B): TransactionId.Value。0 = 未割当。</item>
///   <item><c>Xmax</c> (8B): TransactionId.Value。0 = 未削除。</item>
///   <item><c>Pstamp</c> (8B): η(V)、最新の reader cstamp。SSN 用、未使用時は 0。</item>
///   <item><c>Sstamp</c> (8B): π(V)、上書き tx の cstamp。SSN 用、未上書き時は <see cref="long.MaxValue"/>。</item>
/// </list>
///
/// <para>FT-31 時点では sidecar は配線されていない (record 内の Xmin/Xmax が引き続き正)。
/// FT-32 で record から Xmin/Xmax を撤去し sidecar 経由 access へ移行、FT-33 で SSN protocol が
/// Pstamp/Sstamp を使い始める。</para>
/// </summary>
public readonly record struct EntityVersionMeta(long Xmin, long Xmax, long Pstamp, long Sstamp)
{
    /// <summary>1 エントリのバイトサイズ (32)。</summary>
    public const int Size = 32;

    /// <summary>
    /// 未書き込みエントリの既定値。Xmin/Xmax = 0、Pstamp = 0、Sstamp = <see cref="long.MaxValue"/>。
    /// page を割り当てた直後の 0 埋め状態は Sstamp = 0 になるが、<see cref="IEntityVersionStore.Read"/>
    /// は 0 を <see cref="long.MaxValue"/> に正規化して返すため、呼び出し側からは <c>Unset</c> と
    /// 区別がつかない。
    /// </summary>
    public static readonly EntityVersionMeta Unset = new(0, 0, 0, long.MaxValue);

    /// <summary>初期状態 (まだ Xmin が書かれていない) なら true。</summary>
    public bool IsUnset => Xmin == 0 && Xmax == 0 && Pstamp == 0
        && (Sstamp == 0 || Sstamp == long.MaxValue);
}
