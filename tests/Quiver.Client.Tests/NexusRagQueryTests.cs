using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>RAG の n 項 fact を汎用 nexus DSL だけで取得できることを検証する。</summary>
public sealed class NexusRagQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;
    private readonly VertexId _alice;
    private readonly VertexId _bob;
    private readonly VertexId _quiver;
    private readonly VertexId _databaseEngine;
    private readonly VertexId _chunk;
    private readonly VertexId _secondChunk;
    private readonly VertexId _asOf;
    private readonly NexusId _verifiedFact;
    private readonly NexusId _multiObjectFact;

    public NexusRagQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_nexus_rag_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginWriteTransaction();
        _alice = tx.CreateVertex("Entity");
        _bob = tx.CreateVertex("Entity");
        _quiver = tx.CreateVertex("Entity");
        _databaseEngine = tx.CreateVertex("Entity");
        _chunk = tx.CreateVertex("Chunk");
        _secondChunk = tx.CreateVertex("Chunk");
        _asOf = tx.CreateVertex("TimePoint");

        _verifiedFact = tx.CreateNexus("Fact",
        [
            new NexusMember("subject", _alice),
            new NexusMember("object", _quiver),
            new NexusMember("source", _chunk),
            new NexusMember("asOf", _asOf),
        ]);
        tx.SetProperty(_verifiedFact, "status", PropertyValue.FromString("verified"));

        _multiObjectFact = tx.CreateNexus("Fact",
        [
            new NexusMember("subject", _bob),
            new NexusMember("object", _quiver),
            new NexusMember("object", _databaseEngine),
            new NexusMember("source", _secondChunk),
            new NexusMember("asOf", _asOf),
        ]);
        tx.SetProperty(_multiObjectFact, "status", PropertyValue.FromString("draft"));
        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Subject_query_returns_object_and_source_in_one_operator_tree()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var facts = g.Vertex(_alice)
            .Nexuses("Fact", "subject").As("fact")
            .Members("object").As("object")
            .Select<NexusId>("fact")
            .Members("source").As("source")
            .Select(row => (Object: row.Vertex("object"), Source: row.Vertex("source")));

        facts.Should().ContainSingle()
            .Which.Should().Be((_quiver, _chunk));
    }

    [Fact]
    public void Chunk_query_returns_subject_and_object_in_one_operator_tree()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var facts = g.Vertex(_chunk)
            .Nexuses("Fact", "source").As("fact")
            .Members("subject").As("subject")
            .Select<NexusId>("fact")
            .Members("object").As("object")
            .Select(row => (Subject: row.Vertex("subject"), Object: row.Vertex("object")));

        facts.Should().ContainSingle()
            .Which.Should().Be((_alice, _quiver));
    }

    [Fact]
    public void Property_filter_can_precede_all_role_expansions()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var facts = g.Nexuses()
            .Has("status", "verified").As("fact")
            .Members("subject").As("subject")
            .Select<NexusId>("fact")
            .Members("object").As("object")
            .Select<NexusId>("fact")
            .Members("source").As("source")
            .Select<NexusId>("fact")
            .Members("asOf").As("asOf")
            .Select(row => (
                Subject: row.Vertex("subject"),
                Object: row.Vertex("object"),
                Source: row.Vertex("source"),
                AsOf: row.Vertex("asOf")));

        facts.Should().ContainSingle()
            .Which.Should().Be((_alice, _quiver, _chunk, _asOf));
    }

    [Fact]
    public void Same_role_with_multiple_members_emits_each_member()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var objects = g.Vertex(_bob)
            .Nexuses("Fact", "subject")
            .Members("object")
            .ToList();

        objects.Should().BeEquivalentTo([_quiver, _databaseEngine]);
    }

    [Fact]
    public void Typed_select_rejects_an_alias_with_a_different_entity_kind()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        Action act = () => g.Vertex(_alice).As("vertex").Select<NexusId>("vertex");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NexusId*");
    }
}
