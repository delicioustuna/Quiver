namespace Quiver.Storage.Records;

/// <summary>
/// FT-32: <see cref="IEntityVersionStore"/> の in-memory 実装。
///
/// <para>durable な WAL 連動が不要な経路 (store の単体テスト / ベンチ / tx 外の bulk 操作) で、
/// 各 store がサイドカーを明示注入されなかった場合の既定として使う。本番経路 (binary backend) は
/// <see cref="EntityVersionStore"/> (PagedFile + WAL) を注入するためこの実装は使われない。</para>
///
/// <para>localId をインデックスとする growable 配列で O(1) read/write。未書き込みスロットは
/// <see cref="EntityVersionMeta.Unset"/> を返す (全フィールド 0 を Unset に正規化)。</para>
/// </summary>
internal sealed class InMemoryEntityVersionStore : IEntityVersionStore
{
    private EntityVersionMeta[] _entries = new EntityVersionMeta[256];
    private long _count; // 書き込み済み上限 (= 最大 localId + 1)
    private long _commitStampHighWater; // FT-33: in-memory なので耐久性はないが API 整合のため保持

    /// <inheritdoc/>
    public EntityVersionMeta Read(long localId)
    {
        if (localId < 0 || localId >= _count) return EntityVersionMeta.Unset;
        var m = _entries[localId];
        // page 割り当て直後の 0 埋めと同じく、全フィールド 0 は Unset に正規化する。
        if (m.Xmin == 0 && m.Xmax == 0 && m.Pstamp == 0 && m.Sstamp == 0)
            return EntityVersionMeta.Unset;
        return m;
    }

    /// <inheritdoc/>
    public void Write(long localId, in EntityVersionMeta meta)
    {
        EnsureCapacity(localId);
        _entries[localId] = meta;
    }

    /// <inheritdoc/>
    public void UpdateXmax(long localId, long xmax)
    {
        EnsureCapacity(localId);
        _entries[localId] = _entries[localId] with { Xmax = xmax };
    }

    /// <inheritdoc/>
    public void UpdatePstamp(long localId, long pstamp)
    {
        EnsureCapacity(localId);
        _entries[localId] = _entries[localId] with { Pstamp = pstamp };
    }

    /// <inheritdoc/>
    public void UpdateSstamp(long localId, long sstamp)
    {
        EnsureCapacity(localId);
        _entries[localId] = _entries[localId] with { Sstamp = sstamp };
    }

    /// <inheritdoc/>
    public void WriteCommitStampHighWater(long value) => _commitStampHighWater = value;

    /// <inheritdoc/>
    public long ReadCommitStampHighWater() => _commitStampHighWater;

    /// <inheritdoc/>
    public void Dispose() { }

    private void EnsureCapacity(long localId)
    {
        if (localId < 0)
            throw new ArgumentOutOfRangeException(nameof(localId), "LocalId は非負である必要があります。");
        if (localId >= _entries.Length)
        {
            long newLen = _entries.Length;
            while (newLen <= localId) newLen *= 2;
            Array.Resize(ref _entries, (int)newLen);
        }
        if (localId >= _count) _count = localId + 1;
    }
}
