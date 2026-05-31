namespace Quiver.Storage;

/// <summary>
/// 複数ファイルを跨ぐページ管理(将来の Property/Index ファイル分離に備える)。
/// </summary>
public interface IPageManager : IDisposable
{
    IPagedFile OpenOrCreate(string path, PageKind defaultKind);

    /// <summary>
    /// 管理下の全 <see cref="IPagedFile"/> のダーティページをディスクにフラッシュ (fsync) する。
    /// チェックポイント時に、WAL を truncate する前にデータページの永続化を保証するために使う。
    /// </summary>
    void FlushAll();

    /// <summary>
    /// OP-1: 管理下の全 <see cref="IPagedFile"/> をスナップショット用に列挙する。
    /// 返却順序は無保証。スナップショット中に並行で <see cref="OpenOrCreate"/> / <c>Drop</c>
    /// が走っても列挙器が壊れないよう、実装は配列スナップショットを返す前提。
    /// </summary>
    IReadOnlyList<IPagedFile> Files => Array.Empty<IPagedFile>();
}
