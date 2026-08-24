# Yatagarasu

> 日本語のREADMEは [README_ja.md](README_ja.md) をご覧ください。

[![CI](https://github.com/delicioustuna/Yatagarasu/actions/workflows/ci.yml/badge.svg)](https://github.com/delicioustuna/Yatagarasu/actions/workflows/ci.yml)
[![AOT publish smoke](https://github.com/delicioustuna/Yatagarasu/actions/workflows/aot.yml/badge.svg)](https://github.com/delicioustuna/Yatagarasu/actions/workflows/aot.yml)
[![OS portability](https://github.com/delicioustuna/Yatagarasu/actions/workflows/portability.yml/badge.svg)](https://github.com/delicioustuna/Yatagarasu/actions/workflows/portability.yml)

Yatagarasu is an embedded graph database engine for .NET with integrated vector and full-text search.
It stores property graphs and role-aware n-ary Nexus relationships in a single file and provides source-generated typed mapping, fluent graph traversal, transactional persistence, KNN search, and BM25 search.

The core engine is implemented in pure C#, has no third-party package or unmanaged dependency, and supports NativeAOT.

## Features

- Embedded, in-process operation with no server process
- Single-file property graph storage
- Type-safe mapping generated from `[Vertex]`, `[Edge]`, `[Nexus]`, and `[Property]` models
- Role-aware n-ary Nexus relationships with typed workspace APIs
- Fluent graph traversal and declarative pattern matching
- Single Writer with concurrent Snapshot Readers
- Redo-only WAL recovery and durable commits
- B+Tree scalar indexes with opt-in unique string constraints
- KNN vector search with immutable HNSW segments
- BM25 full-text search with immutable index segments
- Hybrid retrieval for local RAG backends

## Quick start

Define a typed graph model.

```csharp
using Yatagarasu.Api;

[Vertex]
public partial class Person
{
    [Indexed(Unique = true)]
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

Write and query it through a typed workspace. Successful write callbacks commit automatically.

```csharp
using Yatagarasu;

using var graph = GraphWorkspace.Open("people.yata");
graph.Write(write =>
{
    var people = write.Set<Person>();
    var alice = write.Add(people, new Person { Name = "Alice", Age = 30 });
    var bob = write.Add(people, new Person { Name = "Bob", Age = 25 });
    write.Connect(alice, new Knows { Since = "2026" }, bob);
});

IReadOnlyList<Person> known = graph.Read(read =>
    read.Raw.Query.Vertices<Person>()
        .Has(p => p.Name, "Alice")
        .Out<Person>(Knows.GraphType)
        .Where(p => p.Age < 30)
        .ToList());
```

The `Yatagarasu` package includes the model attributes and source generator.
Projects with `ImplicitUsings` enabled receive the `Yatagarasu` and `Yatagarasu.Api` namespaces automatically.

The public version is currently `0.7.0` and remains pre-1.0.

## Name

Yatagarasu is the three-legged guiding crow of Japanese mythology. Its three legs represent the graph, vector, and full-text engines, while its role as Emperor Jimmu's guide reflects the product's purpose: guiding applications to the information they need.

## Local RAG

`Yatagarasu.Rag` provides Document and Chunk ingestion, chunking, re-ingestion, metadata filtering, vector and BM25 fusion, and graph expansion for surrounding context and parent documents.

Embedding generation stays in the calling application and is injected through `IChunkEmbedder`.
Corpus-level ingestion profiles prevent mixed embedding semantics, while selected metadata keys can be promoted to scalar indexes.

See the [RAG sample](samples/Yatagarasu.Samples.Rag/) and the [local RAG cookbook](docs/cookbook.md).

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
| [API reference](docs/api/) | Approved public types and members |
| [As-built specification](docs/spec/00_overview.md) | Current storage and execution contracts |
| [0.7.0 rename guide](docs/operations/06_yatagarasu_rename.md) | Package, namespace, and `.yata` migration |

## Samples

Samples are available under [`samples/`](samples/), including CRUD, typed models, Nexus relationships, traversal, pattern matching, vector search, RAG, hosting, observability, and migrations.

## License

[MIT License](LICENSE)
