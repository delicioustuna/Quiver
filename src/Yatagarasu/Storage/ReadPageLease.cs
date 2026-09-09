using Yatagarasu.Core;

namespace Yatagarasu.Storage;

/// <summary>固定中に再割り当てされない読み取りフレーム の識別情報。</summary>
internal readonly struct ReadPageLease(PageId pageId, int frameIndex, long generation)
{
    internal PageId PageId { get; } = pageId;
    internal int FrameIndex { get; } = frameIndex;
    internal long Generation { get; } = generation;

#if DEBUG
    // ハンドルのコピーを含む二重解放を検出する。Releaseビルドの頻繁に通る経路ではメモリを割り当てない。
    private readonly ReleaseAudit _audit = new();
    private sealed class ReleaseAudit { internal int Released; }
#endif

    internal void ClaimRelease()
    {
#if DEBUG
        if (_audit is null || Interlocked.Exchange(ref _audit.Released, 1) != 0)
            throw new InvalidOperationException("Read page lease has already been released.");
#endif
    }
}
