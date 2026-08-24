using System.Buffers.Binary;
using Yatagarasu.Core;

namespace Yatagarasu.Storage;

/// <summary>
/// export provenance だけに使う optional metadata tenant。
/// tenant が無い旧DBの読み取りでは作成せず、最初の書き込み開始時にだけ生成する。
/// </summary>
internal sealed class DatabaseIdentityStore
{
    internal const byte TenantId = 32;

    private const uint Magic = 0x44495651; // "QVID"
    private const byte Version = 1;
    private static readonly PageId HeaderPage = new(1);

    private readonly SingleFileContainer _container;
    private readonly Lock _gate = new();
    private DatabaseInstanceId? _current;

    internal DatabaseIdentityStore(SingleFileContainer container, bool createIfMissing)
    {
        _container = container;
        if (container.HasTenant(TenantId))
            _current = Read(container.OpenTenant(TenantId, PageKind.Header));
        else if (createIfMissing)
            _current = Create();
    }

    internal bool TryGet(out DatabaseInstanceId databaseInstanceId)
    {
        lock (_gate)
        {
            if (_current is { } current)
            {
                databaseInstanceId = current;
                return true;
            }

            databaseInstanceId = default;
            return false;
        }
    }

    internal DatabaseInstanceId EnsureCreated()
    {
        lock (_gate)
            return _current ??= Create();
    }

    /// <summary>
    /// caller所有のwrite transactionへmetadata pageを書き込み、
    /// commit後の <see cref="PublishCreated"/> までin-memoryには公開しない。
    /// </summary>
    internal DatabaseInstanceId PrepareCreate()
    {
        lock (_gate)
            return _current ?? Create(flush: false);
    }

    /// <summary>durable commitが完了したmetadata IDをreaderへ公開する。</summary>
    internal void PublishCreated(DatabaseInstanceId databaseInstanceId)
    {
        lock (_gate)
        {
            if (_current is { } current && current != databaseInstanceId)
                throw new InvalidOperationException(
                    "Database identity metadata was published with a different UUID.");
            _current = databaseInstanceId;
        }
    }

    private DatabaseInstanceId Create(bool flush = true)
    {
        IPagedFile file = _container.OpenTenant(TenantId, PageKind.Header);
        while (file.PageCount <= HeaderPage.Value)
            file.AllocatePage(PageKind.Header);

        DatabaseInstanceId databaseInstanceId = DatabaseInstanceId.New();
        using (PageWriteHandle header = file.PinForWrite(HeaderPage))
        {
            header.Data[..24].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header.Data, Magic);
            header.Data[4] = Version;
            databaseInstanceId.Value.TryWriteBytes(header.Data[8..24]);
        }
        if (flush)
            _container.Flush();
        return databaseInstanceId;
    }

    private static DatabaseInstanceId Read(IPagedFile file)
    {
        if (file.PageCount <= HeaderPage.Value)
            throw new CorruptionException("Database identity tenant has no header page.");

        using PageReadHandle header = file.PinForRead(HeaderPage);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.Data) != Magic)
            throw new CorruptionException("Database identity metadata has an invalid magic value.");
        byte version = header.Data[4];
        if (version != Version)
            throw new StorageFormatMismatchException("database-identity", version, Version);

        Guid value = new(header.Data[8..24]);
        if (value == Guid.Empty)
            throw new CorruptionException("Database identity metadata contains an empty UUID.");
        return new DatabaseInstanceId(value);
    }
}
