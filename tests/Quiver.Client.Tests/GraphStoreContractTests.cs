using FluentAssertions;
using Xunit;

namespace Quiver.Client.Tests;

public sealed class GraphStoreContractTests
{
    [Fact]
    public void Callback_surface_commits_crud_and_returns_owned_values()
    {
        using var store = GraphStore.OpenMemory();

        VertexKey alice = store.Write(write =>
        {
            VertexKey created = write.CreateVertex("Person");
            write.Set(created, "name", "Alice");
            write.Set(created, "embedding", GraphValue.FromFloatVector([1f, 2f]));
            return created;
        });

        GraphValue name = store.Read(read => read.Get(alice, "name"));
        GraphValue embedding = store.Read(read => read.Get(alice, "embedding"));

        name.AsString().Should().Be("Alice");
        embedding.AsFloatVector().ToArray().Should().Equal(1f, 2f);
        typeof(GraphValue).IsByRefLike.Should().BeFalse();
    }

    [Fact]
    public void Callback_surface_traverses_edges_without_borrowed_cursor()
    {
        using var store = GraphStore.OpenMemory();
        (VertexKey alice, VertexKey bob) = store.Write(write =>
        {
            VertexKey source = write.CreateVertex("Person");
            VertexKey target = write.CreateVertex("Person");
            write.Connect(source, "KNOWS", target);
            return (source, target);
        });

        IReadOnlyList<VertexKey> neighbors = store.Read(
            read => read.GetNeighbors(alice, GraphDirection.Outgoing, "KNOWS"));

        neighbors.Should().Equal(bob);
        neighbors.GetType().IsByRefLike.Should().BeFalse();
    }

    [Fact]
    public void Failed_write_rolls_back_and_exposes_diagnostic_bundle()
    {
        using var store = GraphStore.OpenMemory();
        VertexKey leaked = default;

        GraphOperationException error = Assert.Throws<GraphOperationException>(() =>
            store.Write(write =>
            {
                leaked = write.CreateVertex("Transient");
                throw new InvalidOperationException("stop");
            }));

        store.Read(read => read.Contains(leaked)).Should().BeFalse();
        error.InnerException.Should().BeOfType<InvalidOperationException>();
        error.Diagnostic.Operation.Should().Be("write");
        error.Diagnostic.Phase.Should().Be("callback");
        error.Diagnostic.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        error.Diagnostic.Trace.Should().Contain("stop");
        store.LastDiagnostic.Should().Be(error.Diagnostic);
    }

    [Fact]
    public void Callback_scope_is_invalid_after_callback_returns()
    {
        using var store = GraphStore.OpenMemory();
        GraphReadScope? leaked = null;

        store.Read(read => leaked = read);

        Assert.Throws<ObjectDisposedException>(() => leaked!.Contains(default));
    }

    [Fact]
    public void Callback_reentrancy_on_same_store_is_rejected()
    {
        using var store = GraphStore.OpenMemory();

        GraphOperationException error = Assert.Throws<GraphOperationException>(() =>
            store.Read(_ => store.Read(_ => true)));

        error.InnerException.Should().BeOfType<GraphCallbackReentrancyException>();
    }

    [Fact]
    public void Async_callback_result_is_rejected_and_write_is_rolled_back()
    {
        using var store = GraphStore.OpenMemory();
        VertexKey leaked = default;

        Action operation = () => store.Write<Task>(write =>
            {
                leaked = write.CreateVertex("Transient");
                return Task.CompletedTask;
            });
        GraphOperationException error = Assert.Throws<GraphOperationException>(operation);

        error.InnerException.Should().BeOfType<InvalidOperationException>();
        store.Read(read => read.Contains(leaked)).Should().BeFalse();
    }

    [Fact]
    public void Advanced_read_session_keeps_its_starting_snapshot()
    {
        using var store = GraphStore.OpenMemory();
        VertexKey original = store.Write(write => write.CreateVertex("Original"));
        using GraphReadSession snapshot = store.Advanced.BeginRead();

        VertexKey later = store.Write(write => write.CreateVertex("Later"));

        snapshot.Contains(original).Should().BeTrue();
        snapshot.Contains(later).Should().BeFalse();
        store.Read(read => read.Contains(later)).Should().BeTrue();
    }

    [Fact]
    public void Advanced_write_session_requires_explicit_completion()
    {
        using var store = GraphStore.OpenMemory();
        VertexKey rolledBack;
        using (GraphWriteSession write = store.Advanced.BeginWrite())
            rolledBack = write.CreateVertex("RolledBack");

        store.Read(read => read.Contains(rolledBack)).Should().BeFalse();

        VertexKey committed;
        using (GraphWriteSession write = store.Advanced.BeginWrite())
        {
            committed = write.CreateVertex("Committed");
            write.Commit();
        }
        store.Read(read => read.Contains(committed)).Should().BeTrue();
    }

    [Fact]
    public void Advanced_surface_exposes_owned_diagnostics()
    {
        using var store = GraphStore.OpenMemory();
        store.Write(write => write.CreateVertex("Measured"));

        DatabaseStatistics statistics = store.Advanced.GetStatistics();
        ConsistencyReport consistency = store.Advanced.CheckConsistency();

        statistics.VertexCount.Should().Be(1);
        consistency.IsConsistent.Should().BeTrue();
    }

    [Fact]
    public void Generated_workspace_mapper_supports_typed_add_update_and_related_read()
    {
        using var workspace = GraphWorkspace.OpenMemory();
        (GraphEntity<WorkspacePerson> alice, GraphEntity<WorkspacePerson> bob) = workspace.Write(write =>
        {
            GraphSet<WorkspacePerson> people = write.Set<WorkspacePerson>();
            GraphEntity<WorkspacePerson> source = write.Add(
                people,
                new WorkspacePerson { Name = "Alice", Age = 30, Tags = ["dev", "lead"] });
            GraphEntity<WorkspacePerson> target = write.Add(
                people,
                new WorkspacePerson { Name = "Bob", Age = 25, Tags = ["dev"] });
            write.Connect(source, "KNOWS", target);
            return (source, target);
        });

        workspace.Write(write => write.Update(alice, current => new WorkspacePerson
        {
            Name = current.Name,
            Age = current.Age + 1,
            Tags = ["lead"],
        }));

        WorkspacePerson loaded = workspace.Read(read => read.Get(alice));
        IReadOnlyList<GraphEntity<WorkspacePerson>> related = workspace.Read(
            read => read.Related(alice, "KNOWS", read.Set<WorkspacePerson>()));

        loaded.Age.Should().Be(31);
        loaded.Tags.Should().Equal("lead");
        related.Should().Equal(bob);
    }

    [Fact]
    public void Adopted_query_surface_supports_traversal_typed_predicates_and_match()
    {
        using var workspace = GraphWorkspace.OpenMemory();
        workspace.Write(write =>
        {
            GraphSet<WorkspacePerson> people = write.Set<WorkspacePerson>();
            GraphEntity<WorkspacePerson> alice = write.Add(
                people,
                new WorkspacePerson { Name = "Alice", Age = 31 });
            GraphEntity<WorkspacePerson> bob = write.Add(
                people,
                new WorkspacePerson { Name = "Bob", Age = 25 });
            write.Connect(alice, "KNOWS", bob);
        });

        IReadOnlyList<WorkspacePerson> typed = workspace.Read(read =>
            read.Raw.Query.Vertices<WorkspacePerson>()
                .Where(person => person.Age > 30 && person.Name.StartsWith("A"))
                .ToList());
        IReadOnlyList<string> matched = workspace.Read(read =>
        {
            var pattern = Quiver.Api.Match.GraphPattern.Vertex("a", "WorkspacePerson")
                .Out("KNOWS", Quiver.Api.Match.GraphPattern.Vertex("b", "WorkspacePerson"));
            return read.Raw.Query.Match(pattern)
                .Return(row => row.Get("b", "Name").AsString())
                .ToList();
        });

        typed.Should().ContainSingle().Which.Name.Should().Be("Alice");
        matched.Should().Equal("Bob");
    }
}

[Quiver.Api.Vertex("WorkspacePerson")]
internal partial class WorkspacePerson
{
    [Quiver.Api.Property]
    public string Name { get; set; } = string.Empty;

    [Quiver.Api.Property]
    public int Age { get; set; }

    [Quiver.Api.Property]
    public List<string> Tags { get; set; } = [];
}
