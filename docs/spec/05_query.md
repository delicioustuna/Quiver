# Query Engine

> as-built specification (v1 baseline)

## Architecture {#architecture}

The query engine follows a **Volcano / iterator** model. Logical plans are optimized
and compiled into physical operator trees. Each operator implements `GetNext()` to
pull the next result row.

## GraphKernel {#graph-kernel}

`GraphKernel` (`src/Quiver/Operators/GraphKernel.cs`) provides the core graph traversal
abstraction. It wraps `IGraphAccessMethods.Expand()` so the same algorithm shell works
across different storage access paths.

### Kernel Contract {#kernel-contract}

```csharp
interface IGraphKernel<TState>
{
    void Initialize(NodeId source, ref TState state);
    bool VisitNeighbor(NodeId source, NodeId target, RelationshipId relId,
                       double weightRaw, int depth, ref TState state);
    bool ShouldContinue(int depth, TState state);
}
```

## Physical Operators {#physical-operators}

### Scan Operators {#scan-ops}

| Operator | Description |
|---|---|
| `AllNodesScanOperator` | Sequential scan of all live nodes |
| `NodeByLabelScanOperator` | Filter by label during scan |
| `AllRelationshipsScanOperator` | Sequential scan of all relationships |

### Expand / Traversal {#expand-ops}

| Operator | Description |
|---|---|
| `ExpandOperator` | Single-hop expansion |
| `VariableLengthExpandOperator` | Multi-hop with min/max depth |
| `BfsOperator` | Breadth-first traversal |
| `ShortestPathOperator` | Unweighted shortest path (BFS) |
| `WeightedShortestPathOperator` | Dijkstra-based weighted shortest path |
| `BidirectionalExpandOperator` | Bidirectional BFS for path finding |
| `RelationshipScanExpandOperator` | Expand via relationship scan |
| `RelationshipEndpointOperator` | Resolve relationship endpoints |

### Filter / Set {#filter-ops}

| Operator | Description |
|---|---|
| `FilterOperator` | Predicate-based row filter |
| `BitmapFilterOperator` | Bitmap-accelerated filter |
| `UnionOperator` | Set union of two operator outputs |
| `CoalesceOperator` | First non-empty result from ordered sources |

### Aggregation / Projection {#agg-ops}

| Operator | Description |
|---|---|
| `ProjectOperator` | Column projection / transformation |
| `SortOperator` | In-memory sort |
| `LimitOperator` | Row count limit |

### Full-Text / Vector {#fts-vec-ops}

| Operator | Description |
|---|---|
| `FullTextScanOperator` | BM25-scored full-text search |
| `FilteredFullTextScanOperator` | Full-text search with predicate filter |
| `KnnNodeSourceOperator` | K-nearest-neighbor vector search |
| `FilteredKnnNodeSourceOperator` | KNN with predicate filter |

## Traversal DSL {#traversal-dsl}

`GraphTraversalSource` (`Quiver.Api`) provides a Gremlin-style fluent traversal API:

```csharp
var g = tx.G(schema);
g.V("Person").Has("name", "Alice")
 .Out("KNOWS")
 .Values<string>("name");
```

## Match Pattern {#match}

`Match` compiles Cypher-like pattern expressions into physical operator trees.
Patterns specify node labels, relationship types, and property predicates that
are optimized into index seeks and expand operations.
