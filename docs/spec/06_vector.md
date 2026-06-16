# Vector Search

> as-built specification (v1 baseline)

## Vector Index Specification {#vector-index}

A vector index is defined by:

| Field | Type | Description |
|---|---|---|
| Name | string | Unique identifier |
| Dimensions | int | Vector dimensionality (positive) |
| EntityKind | enum | `Node` or `Relationship` |
| Metric | enum | `Euclidean`, `Cosine`, or `Dot` |

## PersistentVectorStore {#persistent-store}

`PersistentVectorStore` (`src/Quiver/Storage/Records/PersistentVectorStore.cs`) manages
in-file vector storage as a container tenant within the `*.quiver` file.

- **Binding key**: entity `Sequence` (the slot-local part of EntityRef)
- **Generation check**: stale bindings (generation mismatch) are filtered during KNN read
- **Per-index tenants**: catalog + payload + HNSW graph

## HNSW Index {#hnsw}

`HnswIndex` (`src/Quiver/Storage/Records/HnswIndex.cs`) implements the Hierarchical
Navigable Small World graph for approximate nearest neighbor search.

### Parameters {#hnsw-params}

| Parameter | Value |
|---|---|
| M (max neighbors per layer) | 16 |
| Mmax0 (max neighbors at layer 0) | 32 |
| EfConstruction | 200 |
| MaxLayers | 8 |

### On-Disk Layout {#hnsw-layout}

**Header page** (page 1):

| Field | Type |
|---|---|
| EntryPoint | int64 |
| MaxLevel | int32 |
| Count | int64 |
| MaxSeq | int64 |
| FormatVersion | byte (at offset 31) |

**Node records** (fixed 1,164 bytes each):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Present flag |
| 1 | 1 | Level |
| 2 | 4 | Padding |
| 4 | 12 | Neighbor counts (8 x int8, per layer) |
| 12+ | varies | Neighbor arrays: (Mmax0 + (MaxLayers-1) x M) x int64 = 144 entries |

### Operations {#hnsw-ops}

- **Insert**: assigns level via exponential decay, links to nearest neighbors at each layer
- **Search (KNN)**: greedy traversal from entry point, refining through layers; top-k heap
  with presence check and generation filter
- **Delete**: marks node as absent; re-links neighbors on deletion
- **Rebuild**: automatic when tombstone count exceeds live node count

### Limitations {#hnsw-limits}

- Overwrite of existing sequence updates payload only; HNSW graph topology is not re-linked
- Re-linking and physical deletion are deferred to rebuild

## Distance Metrics {#distance}

`VectorScorer` (`src/Quiver/Core/VectorScorer.cs`) computes vector similarity using
SIMD-accelerated `Vector<float>` operations:

| Metric | Formula | Convention |
|---|---|---|
| Dot | `sum(a[i] * b[i])` | Higher = more similar |
| Cosine | `dot / (norm_a * norm_b)` | Higher = more similar |
| Euclidean | `-sum((a[i] - b[i])^2)` | Negated; higher = more similar |

All metrics follow the convention: **higher score = more similar**. Euclidean distance
is negated so that the same max-heap can be used for all metrics.

## Transaction Integration {#tx-integration}

`tx.SetVector(kind, entityId, indexName, vector)` writes a vector within the current
transaction. The write rides the same container WAL as graph mutations, so commit and
rollback are atomic with the rest of the transaction.

## KNN Search {#knn-search}

```csharp
var results = tx.KnnSearch("vec_idx", queryVector, k: 10);
while (results.MoveNext())
{
    NodeId id = results.Current;
    float score = results.CurrentScore;
}
```
