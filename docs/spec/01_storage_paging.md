# Storage & Paging

> as-built specification (v1 baseline)

## Page Format {#page-format}

- **Page size**: 8,192 bytes (`PagedFile.PageSizeConst`)
- **Page header**: `PageHeader.Size` bytes at offset 0 (PageId, PageKind, LSN, CRC32C checksum)
- **Body**: `BodySize = PageSize - HeaderSize` bytes

## PagedFile {#paged-file}

`PagedFile` (`src/Quiver/Storage/PagedFile.cs`) implements `IPagedFile` using memory-mapped files
with a Clock-algorithm buffer pool.

### Buffer Pool {#buffer-pool}

- Default capacity: 256 frames (`DefaultPoolCapacity`)
- Eviction: **Clock (second-chance)** algorithm scans frames by `_clockHand`
- **STEAL policy**: dirty (uncommitted) pages can be evicted to the data file (`EvictFrame`
  writes the frame to the MMF). This is safe because the WAL logs before-images (CLR) for
  undo on crash recovery.

### Pin / Unpin Protocol {#pin-unpin}

| Operation | Lock | Effect |
|---|---|---|
| `PinForRead(PageId)` | Frame read lock | Returns `ReadOnlySpan<byte>`, increments pin count |
| `PinForWrite(PageId)` | Frame write lock | Returns `PageWriteHandle`, captures CLR before-image if WAL enabled |
| `Unpin(PageId)` | Releases read lock | Decrements pin count |
| `UnpinDirty(PageId, lsn)` | Releases write lock | Updates header LSN+checksum, logs PageImage to WAL, marks dirty |

### Page Allocation {#page-allocation}

- **Meta page** (PageId 0): stores free-list head (int64) and logical page count (int64)
- Free pages form a linked list (next-pointer in body[0..7])
- Allocation prefers free-list reuse; falls back to file-end extension
- File grows in 64 MB increments (`GrowthBytes`)
- Allocation bypasses the WAL (writes directly via `MmfWritePageAndSync` with LSN=0)

### Memory-Mapped File {#mmf}

`MemoryMappedFile` + `MemoryMappedViewAccessor` provide the backing storage.
File extension triggers unmap/remap (`EnsureFileSizeAndRemapLocked`).

## Single-File Container {#single-file}

`TenantPagedFile` multiplexes multiple logical stores (nodes, relationships, properties,
indexes, vectors, FT postings, FT norms, catalog) into a single `*.quiver` file.
Each tenant is identified by a `fileKind` byte assigned by the catalog.

### Catalog {#catalog}

The catalog tenant stores the mapping from logical store names (e.g., index names, FT index
names) to their `fileKind` byte. It is itself a tenant within the container and is recovered
during the WAL recovery phase.

## WAL Sidecar {#wal-sidecar}

The WAL lives in a single sidecar file `*.quiver-wal`. Checkpointing flushes dirty pages
and indexes to the data file, then truncates the WAL.

## Checksum {#checksum}

Every page carries a CRC32C checksum in its header. On read, the checksum is validated;
mismatches raise `CorruptionException`.
