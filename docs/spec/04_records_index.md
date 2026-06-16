# Records & Indexes

> as-built specification (v1 baseline)

## Slotted Page Model {#slotted-pages}

All record stores use a slotted-page layout within 8,160-byte page bodies (`PagedFile.BodySize`).
Records are fixed-size per store; slot index = record offset within page.

## Node Store {#node-store}

`NodeStore` (`src/Quiver/Storage/Records/NodeStore.cs`).

**Node record** (15 bytes):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Flags (alive, deleted) |
| 1 | 6 | FirstRelId (first adjacency-list relationship) |
| 7 | 6 | FirstPropId (first property chain entry) |
| 13 | 2 | LabelId |

- **544 records per page** (8160 / 15)
- Version metadata (xmin/xmax) stored in a separate MVCC sidecar (`EntityVersionMeta`)
- Slot reuse via vacuum free list (`OP-3`); logical delete during active tx

## Relationship Store {#rel-store}

Relationships are stored in an adjacency-list structure. Each relationship record links
to next/prev relationships for both source and target nodes, forming a doubly-linked list
per node endpoint.

## Property Store {#property-store}

`PropertyStore` (`src/Quiver/Storage/Records/PropertyStore.cs`).

**Property record** (41 bytes):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Flags |
| 1 | 4 | KeyId (interned property key) |
| 5 | 1 | ValueType |
| 6 | 24 | InlineValue (up to 24 bytes inline) |
| 30 | 5 | SpilloverId (for values > 24 bytes) |
| 35 | 6 | NextPropId (property chain) |

- **199 records per page** (8160 / 41)
- Inline capacity: 24 bytes. Larger values spill to overflow pages.
- MVCC version metadata via `PropertyVersionMeta` sidecar
- Columnar layout with alloc-free read path and version chains

## EntityRef (ID Packing) {#entity-ref}

`EntityRef` (`src/Quiver/Core/Ids.cs`) packs entity identity into a single `long`:

```
Bit layout (MSB → LSB):
[63..60]  EntityKind   (4 bits; Node=0, Relationship=1)
[59..44]  Generation   (16 bits; 0..65535)
[43..0]   Sequence     (44 bits; slot-local ID; 0..17.6 trillion)
```

| Constant | Value |
|---|---|
| `SequenceMask` | `0xFFF_FFFF_FFFF` (44 bits) |
| `MaxGeneration` | 65,535 |

- `PackLocal(seq, gen)` = `(gen << 44) | (seq & SequenceMask)`
- Generation increments on slot reuse after vacuum; prevents ABA aliasing
- Generation overflow (> 65535): slot is permanently retired

## B+Tree Index {#btree}

`BTreeIndex` (`src/Quiver/Index/BTreeIndex.cs`) implements a disk-resident B+Tree.

### Leaf Page Layout {#btree-leaf}

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | EntryCount (int32) |
| 4 | 8 | NextLeaf (int64, -1 = none) |
| 12 | 8 | PrevLeaf (int64) |
| 20+ | var | Entries: KeyLen(int16) + Key(variable) + Value(int64) |

### Internal Page Layout {#btree-internal}

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | KeyCount (int32) |
| 4 | 8 | FirstChildPageId (int64) |
| 12+ | var | Keys: KeyLen(int16) + Key(variable) + ChildPageId(int64) |

### Key Codec {#key-codec}

`KeyCodec` encodes typed property values into comparable byte sequences for B+Tree ordering:

- **StringEquality**: UTF-8 bytes
- **Int64Equality**: big-endian int64 with sign-flip for correct sort order

### Index Kinds {#index-kinds}

| Kind | Key Type | Lookup |
|---|---|---|
| `StringEquality` | string | Exact match via SeekIndex |
| `Int64Equality` | int64 | Exact match via SeekIndex |
| Range indexes | int64 | Range scan via RangeIndex |

### Journaling Modes {#btree-journal}

B+Tree indexes operate in different WAL journaling modes depending on type:

| Mode | Page WAL | CLR | Usage |
|---|---|---|---|
| `Full` | PageImage + coalesce | Before-image captured | Standard indexes |
| `Suppressed` | No PageImage | No CLR | FT postings/norms leaf (logical WAL only) |
| `RedoOnly` | Eager PageImage | No undo | FT structure pages (nested top action) |
