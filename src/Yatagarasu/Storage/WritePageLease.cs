using Yatagarasu.Core;

namespace Yatagarasu.Storage;

/// <summary>固定中に再割り当てされない書き込みフレーム の識別情報。</summary>
internal readonly struct WritePageLease(PageId pageId, int frameIndex, long generation)
{
    internal PageId PageId { get; } = pageId;
    internal int FrameIndex { get; } = frameIndex;
    internal long Generation { get; } = generation;

#if DEBUG
    // 借用ハンドルに移した後も、コピーを通じた二重解放を検出する。
    private readonly ReleaseAudit _audit = new();
    private sealed class ReleaseAudit { internal int Released; }
#endif

    internal void ClaimRelease()
    {
#if DEBUG
        if (_audit is null || Interlocked.Exchange(ref _audit.Released, 1) != 0)
            throw new InvalidOperationException("Write page lease has already been released.");
#endif
    }
}
