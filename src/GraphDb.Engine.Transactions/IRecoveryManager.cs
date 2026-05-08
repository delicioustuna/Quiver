namespace GraphDb.Engine.Transactions;

/// <summary>クラッシュリカバリの実行者。エンジン起動時に1回だけ呼ばれる。</summary>
public interface IRecoveryManager
{
    /// <summary>
    /// WAL を走査し、最後のチェックポイント以降を REDO する。
    /// 未完了トランザクションは破棄(PageImage 方式なので UNDO 不要)。
    /// </summary>
    /// <returns>復旧後の最新 LSN</returns>
    long Recover();
}
