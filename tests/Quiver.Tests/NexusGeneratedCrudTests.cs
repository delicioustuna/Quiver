using System.Collections.Generic;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SourceGenerator が <c>[Nexus]</c> 付与クラスに生成する CRUD を実 DB に接続して
/// 検証する。単一ロール・複数ロール・省略可能ロール・プロパティの往復を網羅する。
/// </summary>
public sealed class NexusGeneratedCrudTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public NexusGeneratedCrudTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hypgen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static GraphVertexRef<T> Ref<T>(VertexId id) where T : IGraphEntity<T> => new(id);

    [Fact]
    public void Single_role_insert_load_update_delete_roundtrips()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId alice, acme;
        NexusId id;
        using (var tx = db.BeginWriteTransaction())
        {
            alice = Person.Insert(tx, new Person { Name = "Alice" });
            acme = Company.Insert(tx, new Company { Name = "Acme" });
            id = Employment.Insert(tx, new Employment
            {
                Employee = Ref<Person>(alice),
                Employer = Ref<Company>(acme),
                Title = "Engineer",
                Year = 2020,
            });
            tx.Commit();
        }

        // Load restores both role bindings and properties.
        using (var ro = db.BeginReadTransaction())
        {
            var e = Employment.Load(ro, id);
            e.Employee.VertexId.Should().Be(alice);
            e.Employer.VertexId.Should().Be(acme);
            e.Title.Should().Be("Engineer");
            e.Year.Should().Be(2020);
        }

        // Update writes only properties; role bindings stay intact.
        using (var tx = db.BeginWriteTransaction())
        {
            Employment.Update(tx, id, new Employment
            {
                Employee = Ref<Person>(alice),
                Employer = Ref<Company>(acme),
                Title = "Senior Engineer",
                Year = 2023,
            });
            tx.Commit();
        }

        using (var ro = db.BeginReadTransaction())
        {
            var e = Employment.Load(ro, id);
            e.Title.Should().Be("Senior Engineer");
            e.Year.Should().Be(2023);
            e.Employee.VertexId.Should().Be(alice);
            e.Employer.VertexId.Should().Be(acme);
        }

        // Delete makes the nexus invisible.
        using (var tx = db.BeginWriteTransaction())
        {
            Employment.Delete(tx, id);
            tx.Commit();
        }

        using (var ro = db.BeginReadTransaction())
        {
            var members = new List<NexusMember>();
            var m = ro.GetMembers(id);
            while (m.MoveNext()) members.Add(m.Current);
            m.Dispose();
            members.Should().BeEmpty();
        }
    }

    [Fact]
    public void Multi_role_and_optional_role_roundtrip()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId a, b, c, loc;
        NexusId id;
        using (var tx = db.BeginWriteTransaction())
        {
            a = Person.Insert(tx, new Person { Name = "A" });
            b = Person.Insert(tx, new Person { Name = "B" });
            c = Person.Insert(tx, new Person { Name = "C" });
            loc = Company.Insert(tx, new Company { Name = "HQ" });

            id = Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphVertexRef<Person>> { Ref<Person>(a), Ref<Person>(b), Ref<Person>(c) },
                Organizer = Ref<Person>(a),
                Venue = Ref<Company>(loc),
                Topic = "Roadmap",
            });
            tx.Commit();
        }

        using (var ro = db.BeginReadTransaction())
        {
            var meeting = Meeting.Load(ro, id);
            // Incidence stores the vertex sequence; identity is compared by VertexId equality
            // (sequence), which is how VertexId defines equality.
            meeting.Attendees.Select(r => r.VertexId).Should().BeEquivalentTo(new[] { a, b, c },
                o => o.Using<VertexId>(ctx => ctx.Subject.Should().Be(ctx.Expectation)).WhenTypeIs<VertexId>());
            meeting.Organizer.VertexId.Should().Be(a);
            meeting.Venue!.Value.VertexId.Should().Be(loc);
            meeting.Topic.Should().Be("Roadmap");
        }
    }

    [Fact]
    public void Optional_role_omitted_when_null()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId a, b;
        NexusId id;
        using (var tx = db.BeginWriteTransaction())
        {
            a = Person.Insert(tx, new Person { Name = "A" });
            b = Person.Insert(tx, new Person { Name = "B" });
            id = Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphVertexRef<Person>> { Ref<Person>(a), Ref<Person>(b) },
                Organizer = Ref<Person>(a),
                Venue = null,
                Topic = "Sync",
            });
            tx.Commit();
        }

        using (var ro = db.BeginReadTransaction())
        {
            var meeting = Meeting.Load(ro, id);
            meeting.Venue.Should().BeNull();
            meeting.Attendees.Should().HaveCount(2);
        }
    }
}

[Vertex("Person")]
internal partial class Person
{
    [Property] public string Name { get; set; } = "";
}

[Vertex("Company")]
internal partial class Company
{
    [Property] public string Name { get; set; } = "";
}

[Nexus("Employment")]
internal partial class Employment
{
    [Role("Employee")] public GraphVertexRef<Person> Employee { get; set; }
    [Role("Employer")] public GraphVertexRef<Company> Employer { get; set; }
    [Property] public string Title { get; set; } = "";
    [Property] public int Year { get; set; }
}

[Nexus("Meeting")]
internal partial class Meeting
{
    [Role("Attendee")] public IReadOnlyList<GraphVertexRef<Person>> Attendees { get; set; } = new List<GraphVertexRef<Person>>();
    [Role("Organizer")] public GraphVertexRef<Person> Organizer { get; set; }
    [Role("Venue")] public GraphVertexRef<Company>? Venue { get; set; }
    [Property] public string Topic { get; set; } = "";
}
