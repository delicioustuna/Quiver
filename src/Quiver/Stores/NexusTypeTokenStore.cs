using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>Nexus型名を独立した token 空間へ永続化する。</summary>
internal sealed class NexusTypeTokenStore : TokenStoreBase<NexusTypeId>
{
    public NexusTypeTokenStore(string filePath) : base(filePath) { }
    public NexusTypeTokenStore(IPagedFile file) : base(file) { }
    protected override NexusTypeId MakeToken(int id) => new(id);
    protected override int GetId(NexusTypeId token) => token.Value;
}
