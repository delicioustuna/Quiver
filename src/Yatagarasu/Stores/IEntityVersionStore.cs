namespace Yatagarasu.Storage.Records;

/// <summary>entity version sidecarの永続化契約です。</summary>
internal interface IEntityVersionStore : IDisposable
{
    EntityVersionMeta Read(long localId);
    void Write(long localId, in EntityVersionMeta meta);
    void UpdateXmax(long localId, long xmax);
    bool AnyGenerationReuse { get; }
    void MarkGenerationReuse();
}
