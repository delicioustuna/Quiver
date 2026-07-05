using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>RAG の n 項 fact を汎用 hyperedge DSL だけで取得できることを検証する。</summary>
public sealed class HyperedgeRagQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;
    private readonly NodeId _alice;
    private readonly NodeId _bob;
    private readonly NodeId _quiver;
    private readonly NodeId _graphDatabase;
    private readonly NodeId _chunk;
    private readonly NodeId _secondChunk;
    private readonly NodeId _asOf;
    private readonly HyperedgeId _verifiedFact;
    private readonly HyperedgeId _multiObjectFact;

    public HyperedgeRagQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyperedge_rag_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        _alice = tx.CreateNode("Entity");
        _bob = tx.CreateNode("Entity");
        _quiver = tx.CreateNode("Entity");
        _graphDatabase = tx.CreateNode("Entity");
        _chunk = tx.CreateNode("Chunk");
        _secondChunk = tx.CreateNode("Chunk");
        _asOf = tx.CreateNode("TimePoint");

        _verifiedFact = tx.CreateHyperedge("Fact",
        [
            new HyperedgeMember("subject", _alice),
            new HyperedgeMember("object", _quiver),
            new HyperedgeMember("source", _chunk),
            new HyperedgeMember("asOf", _asOf),
        ]);
        tx.SetProperty(_verifiedFact, "status", PropertyValue.FromString("verified"));

        _multiObjectFact = tx.CreateHyperedge("Fact",
        [
            new HyperedgeMember("subject", _bob),
            new HyperedgeMember("object", _quiver),
            new HyperedgeMember("object", _graphDatabase),
            new HyperedgeMember("source", _secondChunk),
            new HyperedgeMember("asOf", _asOf),
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
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var facts = g.Node(_alice)
            .Hyperedges("Fact", "subject").As("fact")
            .Members("object").As("object")
            .Select<HyperedgeId>("fact")
            .Members("source").As("source")
            .Select(row => (Object: row.Node("object"), Source: row.Node("source")));

        facts.Should().ContainSingle()
            .Which.Should().Be((_quiver, _chunk));
    }

    [Fact]
    public void Chunk_query_returns_subject_and_object_in_one_operator_tree()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var facts = g.Node(_chunk)
            .Hyperedges("Fact", "source").As("fact")
            .Members("subject").As("subject")
            .Select<HyperedgeId>("fact")
            .Members("object").As("object")
            .Select(row => (Subject: row.Node("subject"), Object: row.Node("object")));

        facts.Should().ContainSingle()
            .Which.Should().Be((_alice, _quiver));
    }

    [Fact]
    public void Property_filter_can_precede_all_role_expansions()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var facts = g.Hyperedges()
            .Has("status", "verified").As("fact")
            .Members("subject").As("subject")
            .Select<HyperedgeId>("fact")
            .Members("object").As("object")
            .Select<HyperedgeId>("fact")
            .Members("source").As("source")
            .Select<HyperedgeId>("fact")
            .Members("asOf").As("asOf")
            .Select(row => (
                Subject: row.Node("subject"),
                Object: row.Node("object"),
                Source: row.Node("source"),
                AsOf: row.Node("asOf")));

        facts.Should().ContainSingle()
            .Which.Should().Be((_alice, _quiver, _chunk, _asOf));
    }

    [Fact]
    public void Same_role_with_multiple_members_emits_each_member()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var objects = g.Node(_bob)
            .Hyperedges("Fact", "subject")
            .Members("object")
            .ToList();

        objects.Should().BeEquivalentTo([_quiver, _graphDatabase]);
    }

    [Fact]
    public void Typed_select_rejects_an_alias_with_a_different_entity_kind()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        Action act = () => g.Node(_alice).As("node").Select<HyperedgeId>("node");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HyperedgeId*");
    }
}
