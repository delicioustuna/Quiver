using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>ハイパーエッジ型名を独立した token 空間へ永続化する。</summary>
internal sealed class HyperedgeTypeTokenStore : TokenStoreBase<HyperedgeTypeId>
{
    public HyperedgeTypeTokenStore(string filePath) : base(filePath) { }
    public HyperedgeTypeTokenStore(IPagedFile file) : base(file) { }
    protected override HyperedgeTypeId MakeToken(int id) => new(id);
    protected override int GetId(HyperedgeTypeId token) => token.Value;
}
