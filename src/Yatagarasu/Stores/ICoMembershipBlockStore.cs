using Yatagarasu.Core;

namespace Yatagarasu.Storage.Records;

/// <summary>
/// 明示されたロール対について、起点Vertexから同じNexusのメンバーへ
/// 直接展開する導出ビューの内部契約。
/// </summary>
internal interface ICoMembershipBlockStore
{
    /// <summary>指定ロール対が物理化されているかを返す。</summary>
    bool Contains(RoleId originRole, RoleId memberRole);

    /// <summary>起点Vertexとロール対に対応する連続エントリを取得する。</summary>
    CoMembershipEntry[] GetEntries(
        VertexId originVertex,
        RoleId originRole,
        RoleId memberRole,
        out int count);

    /// <summary>新しいNexusから対象ロール対の差分を追加する。</summary>
    void Add(NexusId nexusId, ReadOnlySpan<IncidenceMember> members);

    /// <summary>正本の header と incidence からビュー全体を再構築する。</summary>
    void Rebuild(INexusStore nexuses, IIncidenceStore incidences);

    /// <summary>差分反映に失敗したビューを無効化し、以後の読み取りを fallback させる。</summary>
    void Invalidate();
}

/// <summary>物理 co-membership ビューの 1 エントリ。</summary>
internal readonly record struct CoMembershipEntry(NexusId NexusId, VertexId MemberVertexId);
