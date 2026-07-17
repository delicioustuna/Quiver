using Quiver.Core;

namespace Quiver.Storage.Wal;

/// <summary>
/// 一つの書き込みトランザクションが所有する page image と in-process undo の集合。
/// WAL には commit 直前の after-image だけを出し、勝者は明示的な Commit レコードだけで決める。
/// </summary>
internal sealed class WalWriteSet(IWriteAheadLog wal, TransactionId txId)
{
    private readonly Dictionary<(byte FileKind, long PageId), byte[]> _pending = new();
    private readonly List<Dictionary<(byte FileKind, long PageId), byte[]>> _beforeImageStack =
        [new Dictionary<(byte FileKind, long PageId), byte[]>()];

    public int Depth => _beforeImageStack.Count;
    internal TransactionId TransactionId => txId;

    public long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var key = (fileKind, pageId);
        if (!_pending.TryGetValue(key, out var buffer) || buffer.Length != pageBytes.Length)
            _pending[key] = buffer = new byte[pageBytes.Length];
        pageBytes.CopyTo(buffer);
        return -1L;
    }

    public void CaptureBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var key = (fileKind, pageId);
        var currentBucket = _beforeImageStack[^1];
        if (!currentBucket.ContainsKey(key))
            currentBucket[key] = WalPageImageCodec.Encode(fileKind, pageId, pageBytes);
    }

    public void FlushPending()
    {
        foreach (var entry in _pending)
        {
            byte[] payload = WalPageImageCodec.Encode(entry.Key.FileKind, entry.Key.PageId, entry.Value);
            wal.BufferPageImage(txId, entry.Key.FileKind, entry.Key.PageId, payload);
        }
        _pending.Clear();
    }

    public IReadOnlyCollection<byte[]> GetAllBeforeImagesOldestWins()
    {
        if (_beforeImageStack.Count == 1) return _beforeImageStack[0].Values;

        var merged = new Dictionary<(byte FileKind, long PageId), byte[]>();
        foreach (var bucket in _beforeImageStack)
        {
            foreach (var entry in bucket)
                merged.TryAdd(entry.Key, entry.Value);
        }
        return merged.Values;
    }

    public int PushSavepoint()
    {
        _beforeImageStack.Add(new Dictionary<(byte FileKind, long PageId), byte[]>());
        return _beforeImageStack.Count - 1;
    }

    public IReadOnlyCollection<byte[]> RollbackToSavepoint(int level)
    {
        if (level <= 0 || level >= _beforeImageStack.Count)
            return Array.Empty<byte[]>();

        var merged = new Dictionary<(byte FileKind, long PageId), byte[]>();
        for (int i = level; i < _beforeImageStack.Count; i++)
        {
            foreach (var entry in _beforeImageStack[i])
                merged.TryAdd(entry.Key, entry.Value);
        }

        _beforeImageStack.RemoveRange(level, _beforeImageStack.Count - level);
        _beforeImageStack.Add(new Dictionary<(byte FileKind, long PageId), byte[]>());
        return merged.Values;
    }

    public void ReleaseSavepoint(int level)
    {
        if (level <= 0 || level >= _beforeImageStack.Count) return;

        var parent = _beforeImageStack[level - 1];
        foreach (var entry in _beforeImageStack[level])
            parent.TryAdd(entry.Key, entry.Value);
        _beforeImageStack.RemoveAt(level);
    }

    public void OverwritePendingFromBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => LogPageImage(fileKind, pageId, pageBytes);
}
