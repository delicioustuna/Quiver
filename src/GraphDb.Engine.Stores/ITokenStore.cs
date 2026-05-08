using GraphDb.Engine.Core;

namespace GraphDb.Engine.Stores;

public interface ITokenStore<TToken> where TToken : struct
{
    TToken GetOrCreate(ReadOnlySpan<char> name);
    bool TryGet(ReadOnlySpan<char> name, out TToken token);
    ReadOnlySpan<byte> GetNameUtf8(TToken token);
    string GetName(TToken token);
    IEnumerable<TToken> All();
}

public sealed class LabelTokenStore : ITokenStore<LabelId>
{
    public LabelId GetOrCreate(ReadOnlySpan<char> name) => throw new NotImplementedException();
    public bool TryGet(ReadOnlySpan<char> name, out LabelId token) => throw new NotImplementedException();
    public ReadOnlySpan<byte> GetNameUtf8(LabelId token) => throw new NotImplementedException();
    public string GetName(LabelId token) => throw new NotImplementedException();
    public IEnumerable<LabelId> All() => throw new NotImplementedException();
}

public sealed class RelationshipTypeTokenStore : ITokenStore<RelationshipTypeId>
{
    public RelationshipTypeId GetOrCreate(ReadOnlySpan<char> name) => throw new NotImplementedException();
    public bool TryGet(ReadOnlySpan<char> name, out RelationshipTypeId token) => throw new NotImplementedException();
    public ReadOnlySpan<byte> GetNameUtf8(RelationshipTypeId token) => throw new NotImplementedException();
    public string GetName(RelationshipTypeId token) => throw new NotImplementedException();
    public IEnumerable<RelationshipTypeId> All() => throw new NotImplementedException();
}

public sealed class PropertyKeyTokenStore : ITokenStore<PropertyKeyId>
{
    public PropertyKeyId GetOrCreate(ReadOnlySpan<char> name) => throw new NotImplementedException();
    public bool TryGet(ReadOnlySpan<char> name, out PropertyKeyId token) => throw new NotImplementedException();
    public ReadOnlySpan<byte> GetNameUtf8(PropertyKeyId token) => throw new NotImplementedException();
    public string GetName(PropertyKeyId token) => throw new NotImplementedException();
    public IEnumerable<PropertyKeyId> All() => throw new NotImplementedException();
}
