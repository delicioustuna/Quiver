using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Stores;

internal sealed class PropertyStore : IPropertyStore
{
    public const int RecordSize = 41;

    private readonly IPagedFile _file;
    private readonly BlobStore _blobs;

    public PropertyStore(IPagedFile file, IPagedFile blobFile)
    {
        _file = file;
        _blobs = new BlobStore(blobFile);
    }

    public PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst)
        => throw new NotImplementedException();

    public PropertyId Delete(PropertyId propId, PropertyId currentFirst)
        => throw new NotImplementedException();

    public PropertyReadHandle Read(PropertyId propId)
        => throw new NotImplementedException();

    public PropertyEnumerator Enumerate(PropertyId firstPropId)
        => throw new NotImplementedException();
}
