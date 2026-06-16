# Quiver: System Overview

> as-built specification (v1 baseline, 2026-06-16)

## Positioning {#positioning}

Quiver is a **pure C# embedded (in-process) graph + vector + full-text search database engine** targeting .NET.
The comparison axis is SQLite / LiteDB / KuzuDB-class embedded DBs, not server-scale or distributed graph systems.

## Primary Use Case {#use-case}

**Local RAG backend** -- but the engine itself is general-purpose; the RAG-specific API lives in `Quiver.Rag`.

## Zero-Dependency Thesis {#zero-dep}

Only LLM model driving is external. All other components (storage, WAL, recovery, indexing, vector search,
full-text search, query engine) are implemented from scratch in managed C# with zero third-party dependencies.

**Trusted Computing Base (TCB):**

| Component | Trust Basis |
|---|---|
| .NET BCL | Platform runtime |
| Claude | Implementer (all code authored by AI under human review) |
| LLM provider | Runtime dependency (embedding / generation, via `Quiver.Embedding`) |

Managed C# eliminates the memory-safety vulnerability class that affects C/C++ storage engines.
Tests serve as security controls -- they are the primary verification mechanism.

## Architecture Layers {#layers}

```
┌─────────────────────────────────────────────────┐
│  Quiver.Rag / Quiver.Embedding / Quiver.Hosting │  Optional add-ons
├─────────────────────────────────────────────────┤
│  GraphDatabase (facade)                         │
│  ├─ ISchemaApi (labels, indexes, FT indexes)    │
│  ├─ IGraphTransaction (CRUD, index, vector)     │
│  ├─ IDiagnosticsApi (consistency check, repair) │
│  └─ Logical mutation sink (audit / replication) │
├─────────────────────────────────────────────────┤
│  Query Engine                                   │
│  ├─ Logical IR + Optimizer                      │
│  ├─ Volcano physical operators                  │
│  └─ GraphKernel (BFS/DFS/shortest-path)         │
├─────────────────────────────────────────────────┤
│  Transaction Manager                            │
│  ├─ MVCC (snapshot isolation)                   │
│  ├─ ARIES WAL + recovery                        │
│  └─ Checkpointer                                │
├─────────────────────────────────────────────────┤
│  Storage Engine                                 │
│  ├─ PagedFile (8 KB pages, Clock buffer pool)   │
│  ├─ SingleFileContainer (*.quiver)              │
│  ├─ NodeStore / RelationshipStore / PropertyStore│
│  ├─ B+Tree indexes                              │
│  ├─ FullTextIndex (postings + norms B+Trees)    │
│  └─ PersistentVectorStore + HNSW                │
└─────────────────────────────────────────────────┘
```

## Assemblies {#assemblies}

| Assembly | Role |
|---|---|
| `Quiver` | Engine core (single assembly, all subsystems) |
| `Quiver.Client.Attributes` | Source-generator attributes |
| `Quiver.SourceGen` | Roslyn source generator for typed graph models |
| `Quiver.Embedding` | Vector / embedding pipeline (KNN, hybrid search) |
| `Quiver.Rag` | Local RAG layer (Document/Chunk schema, ingest, hybrid search + graph expansion) |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting` integration (DI) |
| `Quiver.OpenTelemetry` | OpenTelemetry export |

## File Layout {#file-layout}

At rest, a Quiver database is a **single file** `*.quiver`. During operation, a WAL sidecar
`*.quiver-wal` exists alongside it. On clean shutdown the WAL is empty or absent.

## Format Version {#format-version}

`FormatVersion.Current = V1 = 1`. No automatic migration; opening a database with a different
format version throws `FormatVersionMismatchException`.

## Non-Goals {#non-goals}

- Server process / network protocol
- Distributed / sharded deployment
- SQL query language
- Automatic schema migration for on-disk format changes (pre-1.0)
