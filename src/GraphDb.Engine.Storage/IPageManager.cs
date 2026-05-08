namespace GraphDb.Engine.Storage;

/// <summary>
/// 複数ファイルを跨ぐページ管理(将来の Property/Index ファイル分離に備える)。
/// </summary>
public interface IPageManager : IDisposable
{
    IPagedFile OpenOrCreate(string path, PageKind defaultKind);
}
