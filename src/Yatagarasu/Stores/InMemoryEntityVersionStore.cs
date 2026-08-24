namespace Yatagarasu.Storage.Records;

internal sealed class InMemoryEntityVersionStore : IEntityVersionStore
{
    private EntityVersionMeta[] _entries = new EntityVersionMeta[256];
    private long _count;
    private bool _anyReuse;

    public EntityVersionMeta Read(long localId)
    {
        if (localId < 0 || localId >= _count) return EntityVersionMeta.Unset;
        EntityVersionMeta metadata = _entries[localId];
        return metadata.IsUnset ? EntityVersionMeta.Unset : metadata;
    }

    public void Write(long localId, in EntityVersionMeta meta)
    {
        EnsureCapacity(localId);
        _entries[localId] = meta;
    }

    public void UpdateXmax(long localId, long xmax)
    {
        EnsureCapacity(localId);
        _entries[localId] = _entries[localId] with { Xmax = xmax };
    }

    public bool AnyGenerationReuse => _anyReuse;
    public void MarkGenerationReuse() => _anyReuse = true;
    public void Dispose() { }

    private void EnsureCapacity(long localId)
    {
        if (localId < 0)
            throw new ArgumentOutOfRangeException(nameof(localId));
        if (localId >= _entries.Length)
        {
            long length = _entries.Length;
            while (length <= localId) length *= 2;
            Array.Resize(ref _entries, checked((int)length));
        }
        if (localId >= _count) _count = localId + 1;
    }
}
