using System.Collections.Generic;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SourceGenerator が <c>[Hyperedge]</c> 付与クラスに生成する CRUD を実 DB に接続して
/// 検証する。単一ロール・複数ロール・省略可能ロール・プロパティの往復を網羅する。
/// </summary>
public sealed class HyperedgeGeneratedCrudTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public HyperedgeGeneratedCrudTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hypgen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Single_role_insert_load_update_delete_roundtrips()
    {
        using var db = GraphDatabase.Open(_path);

        NodeId alice, acme;
        HyperedgeId id;
        using (var tx = db.BeginTransaction())
        {
            alice = Person.Insert(tx, new Person { Name = "Alice" });
            acme = Company.Insert(tx, new Company { Name = "Acme" });
            id = Employment.Insert(tx, new Employment
            {
                Employee = alice,
                Employer = acme,
                Title = "Engineer",
                Year = 2020,
            });
            tx.Commit();
        }

        // Load restores both role bindings and properties.
        using (var ro = db.BeginReadOnlyTransaction())
        {
            var e = Employment.Load(ro, id);
            e.Employee.NodeId.Should().Be(alice);
            e.Employer.NodeId.Should().Be(acme);
            e.Title.Should().Be("Engineer");
            e.Year.Should().Be(2020);
        }

        // Update writes only properties; role bindings stay intact.
        using (var tx = db.BeginTransaction())
        {
            Employment.Update(tx, id, new Employment
            {
                Employee = alice,
                Employer = acme,
                Title = "Senior Engineer",
                Year = 2023,
            });
            tx.Commit();
        }

        using (var ro = db.BeginReadOnlyTransaction())
        {
            var e = Employment.Load(ro, id);
            e.Title.Should().Be("Senior Engineer");
            e.Year.Should().Be(2023);
            e.Employee.NodeId.Should().Be(alice);
            e.Employer.NodeId.Should().Be(acme);
        }

        // Delete makes the hyperedge invisible.
        using (var tx = db.BeginTransaction())
        {
            Employment.Delete(tx, id);
            tx.Commit();
        }

        using (var ro = db.BeginReadOnlyTransaction())
        {
            var members = new List<HyperedgeMember>();
            var m = ro.GetMembers(id);
            while (m.MoveNext()) members.Add(m.Current);
            m.Dispose();
            members.Should().BeEmpty();
        }
    }

    [Fact]
    public void Multi_role_and_optional_role_roundtrip()
    {
        using var db = GraphDatabase.Open(_path);

        NodeId a, b, c, loc;
        HyperedgeId id;
        using (var tx = db.BeginTransaction())
        {
            a = Person.Insert(tx, new Person { Name = "A" });
            b = Person.Insert(tx, new Person { Name = "B" });
            c = Person.Insert(tx, new Person { Name = "C" });
            loc = Company.Insert(tx, new Company { Name = "HQ" });

            id = Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphNodeRef<Person>> { a, b, c },
                Organizer = a,
                Venue = loc,
                Topic = "Roadmap",
            });
            tx.Commit();
        }

        using (var ro = db.BeginReadOnlyTransaction())
        {
            var meeting = Meeting.Load(ro, id);
            // Incidence stores the node sequence; identity is compared by NodeId equality
            // (sequence), which is how NodeId defines equality.
            meeting.Attendees.Select(r => r.NodeId).Should().BeEquivalentTo(new[] { a, b, c },
                o => o.Using<NodeId>(ctx => ctx.Subject.Should().Be(ctx.Expectation)).WhenTypeIs<NodeId>());
            meeting.Organizer.NodeId.Should().Be(a);
            meeting.Venue!.Value.NodeId.Should().Be(loc);
            meeting.Topic.Should().Be("Roadmap");
        }
    }

    [Fact]
    public void Optional_role_omitted_when_null()
    {
        using var db = GraphDatabase.Open(_path);

        NodeId a, b;
        HyperedgeId id;
        using (var tx = db.BeginTransaction())
        {
            a = Person.Insert(tx, new Person { Name = "A" });
            b = Person.Insert(tx, new Person { Name = "B" });
            id = Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphNodeRef<Person>> { a, b },
                Organizer = a,
                Venue = null,
                Topic = "Sync",
            });
            tx.Commit();
        }

        using (var ro = db.BeginReadOnlyTransaction())
        {
            var meeting = Meeting.Load(ro, id);
            meeting.Venue.Should().BeNull();
            meeting.Attendees.Should().HaveCount(2);
        }
    }
}

[Node("Person")]
internal partial class Person
{
    [Property] public string Name { get; set; } = "";
}

[Node("Company")]
internal partial class Company
{
    [Property] public string Name { get; set; } = "";
}

[Hyperedge("Employment")]
internal partial class Employment
{
    [Role("Employee")] public GraphNodeRef<Person> Employee { get; set; }
    [Role("Employer")] public GraphNodeRef<Company> Employer { get; set; }
    [Property] public string Title { get; set; } = "";
    [Property] public int Year { get; set; }
}

[Hyperedge("Meeting")]
internal partial class Meeting
{
    [Role("Attendee")] public IReadOnlyList<GraphNodeRef<Person>> Attendees { get; set; } = new List<GraphNodeRef<Person>>();
    [Role("Organizer")] public GraphNodeRef<Person> Organizer { get; set; }
    [Role("Venue")] public GraphNodeRef<Company>? Venue { get; set; }
    [Property] public string Topic { get; set; } = "";
}
