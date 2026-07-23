# Quiver

> 日本語のREADMEは [README_ja.md](README_ja.md) をご覧ください。

[![CI](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml)
[![AOT publish smoke](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml)

Quiver is an embedded graph database engine for .NET with integrated vector and full-text search.
It stores property graphs in a single file and provides type-safe CRUD through a source generator, fluent graph traversal, transactional persistence, KNN search, and BM25 search.

The core engine is implemented in pure C#, has no third-party package or unmanaged dependency, and supports NativeAOT.

## Features

- Embedded, in-process operation with no server process
- Single-file property graph storage
- Type-safe APIs generated from `[Vertex]`, `[Edge]`, and `[Property]` models
- Fluent graph traversal and declarative pattern matching
- Single Writer with concurrent Snapshot Readers
- Redo-only WAL recovery and durable commits
- B+Tree scalar indexes
- KNN vector search with immutable HNSW segments
- BM25 full-text search with immutable index segments
- Hybrid retrieval for local RAG backends

## Quick start

Define a typed graph model.

```csharp
using Quiver.Api;

[Vertex]
public partial class Person
{
    [Indexed]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

[Edge<Person, Person>]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}
```

Query it through a snapshot-bound read transaction.

```csharp
using var tx = db.BeginReadTransaction();

var known = tx.Query.Vertices<Person>()
    .Has(p => p.Name, "Alice")
    .Knows()
    .Has(p => p.Age, P.Lt(30L))
    .ToList();
```

The `Quiver` package includes the model attributes and source generator.
Projects with `ImplicitUsings` enabled receive the `Quiver` and `Quiver.Api` namespaces automatically.

The public version is currently `0.1.0` and remains pre-1.0.

## Local RAG

`Quiver.Rag` provides Document and Chunk ingestion, chunking, re-ingestion, metadata filtering, vector and BM25 fusion, and graph expansion for surrounding context and parent documents.

Embedding generation stays in the calling application and is injected through `IChunkEmbedder`.

See the [RAG sample](samples/Quiver.Samples.Rag/) and the [local RAG cookbook](docs/cookbook.md).

## Reference performance

| Operation | Measured result |
|---|---:|
| Vertex insert, amortized in one transaction | ~3.5–4 µs/op |
| Vertex insert with properties | ~6 µs/op |
| Edge insert | ~7 µs/op |
| Durable single-operation commit | ~1.0 ms/commit |
| One-hop scan, degree 100, adjacency segment | ~0.35 µs |
| One-hop fluent query, degree 100 | ~4.2 µs/query |
| BulkLoader, 100,000 edges | ~11.8× regular batched transactions |
| HNSW true recall@10, N=10,000, dim=384 | 0.950 |

Results were measured in process on an AMD Ryzen 7 5700X with .NET 10.
They are reference values rather than cross-machine guarantees.
See the [benchmark summary](docs/benchmark-results.md) for details.

## Limitations

| Constraint | Behavior |
|---|---|
| Single writer | One write transaction runs at a time; readers continue on independent snapshots |
| In-process only | One process opens a database file exclusively; no network protocol is included |
| No automatic physical format migration | Databases with an incompatible format must be rebuilt from source data |
| Derived vector rebuild | Exact scan is used while immutable HNSW state is unavailable or being rebuilt |

See the [known limits](docs/spec/08_known_limits.md) for the complete contract.

## Documentation

| Document | Contents |
|---|---|
| [Getting Started](docs/api/getting-started.md) | Installation and first database |
| [Concepts](docs/api/concepts/index.md) | Transactions, traversal, matching, indexes, and search |
| [Tutorials](docs/api/tutorials/index.md) | Task-oriented examples |
| [Cookbook](docs/cookbook.md) | Common graph and RAG recipes |
| [Operations](docs/operations/README.md) | Backup, recovery, and performance tuning |
| [Architecture](docs/architecture.md) | System structure and data flow |
| [As-built specification](docs/spec/00_overview.md) | Current storage and execution contracts |
| [Development](docs/design/development.md) | Build, test, packaging, and repository rules |

## Samples

Samples are available under [`samples/`](samples/), including CRUD, typed models, traversal, pattern matching, vector search, RAG, hosting, observability, and migrations.

## License

[MIT License](LICENSE)
