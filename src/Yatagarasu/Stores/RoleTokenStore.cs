using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>incidence role 名を edge / nexus type と独立した token 空間へ永続化する。</summary>
internal sealed class RoleTokenStore : TokenStoreBase<RoleId>
{
    public RoleTokenStore(string filePath) : base(filePath) { }
    public RoleTokenStore(IPagedFile file) : base(file) { }
    protected override RoleId MakeToken(int id) => new(id);
    protected override int GetId(RoleId token) => token.Value;
}
