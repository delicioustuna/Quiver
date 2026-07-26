using System.Collections.Generic;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SourceGenerator が生成する型保存Nexusトラバーサル糖衣を実 DB に接続して
/// 検証する。型付き走査は型なし DSL と同じ論理オペレータへ委譲するため、
/// 両者の結果集合が一致することを単一・複数・省略可能ロール、同一Vertex型の
/// 複数ロール、プロパティフィルタの各形状で確認する。
/// </summary>
public sealed class NexusTypedTraversalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public NexusTypedTraversalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyptyped_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static List<long> Ids(List<VertexId> ids) => ids.ConvertAll(id => (long)id.Value);

    private static List<long> Ids<T>(List<(VertexId Id, T Entity)> pairs) where T : IGraphVertex<T>
        => pairs.ConvertAll(p => (long)p.Id.Value);

    [Fact]
    public void Single_role_typed_traversal_matches_untyped()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId alice, acme;
        using (var tx = db.BeginWriteTransaction())
        {
            alice = Person.Insert(tx, new Person { Name = "Alice" });
            acme = Company.Insert(tx, new Company { Name = "Acme" });
            Employment.Insert(tx, new Employment
            {
                Employee = alice,
                Employer = acme,
                Title = "Engineer",
                Year = 2020,
            });
            tx.Commit();
        }

        using var ro = db.BeginReadTransaction();
        var g = ro.Query;

        var typed = g.Vertices<Person>().Has(p => p.Name, "Alice")
            .EmploymentAsEmployee().Employer().ToListWithIds();
        var untyped = g.Vertex(alice)
            .Nexuses("Employment", "Employee").Members("Employer").ToList();

        Ids(typed).Should().BeEquivalentTo(Ids(untyped));
        Ids(typed).Should().BeEquivalentTo(Ids(new List<VertexId> { acme }));
        // 型付き終端はエンティティを復元する。
        typed.Should().ContainSingle().Which.Entity.Name.Should().Be("Acme");
    }

    [Fact]
    public void Multi_role_typed_traversal_matches_untyped()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId a, b, c;
        using (var tx = db.BeginWriteTransaction())
        {
            a = Person.Insert(tx, new Person { Name = "A" });
            b = Person.Insert(tx, new Person { Name = "B" });
            c = Person.Insert(tx, new Person { Name = "C" });
            var hq = Company.Insert(tx, new Company { Name = "HQ" });
            Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphVertexRef<Person>> { a, b, c },
                Organizer = a,
                Venue = hq,
                Topic = "Roadmap",
            });
            tx.Commit();
        }

        using var ro = db.BeginReadTransaction();
        var g = ro.Query;

        // 複数メンバーロールは各メンバーVertexを個別の行として放出する。
        var typed = g.Vertices<Person>().Has(p => p.Name, "A")
            .MeetingAsAttendees().Attendees().ToListWithIds();
        var untyped = g.Vertex(a)
            .Nexuses("Meeting", "Attendee").Members("Attendee").ToList();

        Ids(typed).Should().BeEquivalentTo(Ids(untyped));
        Ids(typed).Should().BeEquivalentTo(Ids(new List<VertexId> { a, b, c }));

        // co-membership: 起点Vertexを全ロールから除外する。
        var typedOthers = g.Vertices<Person>().Has(p => p.Name, "A")
            .MeetingAsAttendees().OtherAttendees().ToListWithIds();
        var untypedOthers = g.Vertex(a)
            .Nexuses("Meeting", "Attendee").OtherMembers("Attendee").ToList();

        Ids(typedOthers).Should().BeEquivalentTo(Ids(untypedOthers));
        Ids(typedOthers).Should().BeEquivalentTo(Ids(new List<VertexId> { b, c }));
    }

    [Fact]
    public void Optional_role_typed_traversal_matches_untyped()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId a, hq;
        using (var tx = db.BeginWriteTransaction())
        {
            a = Person.Insert(tx, new Person { Name = "A" });
            var b = Person.Insert(tx, new Person { Name = "B" });
            hq = Company.Insert(tx, new Company { Name = "HQ" });
            // Venue あり / なしの 2 件。省略側は Venue ロールの行を放出しない。
            Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphVertexRef<Person>> { a, b },
                Organizer = a,
                Venue = hq,
                Topic = "With venue",
            });
            Meeting.Insert(tx, new Meeting
            {
                Attendees = new List<GraphVertexRef<Person>> { a, b },
                Organizer = a,
                Venue = null,
                Topic = "No venue",
            });
            tx.Commit();
        }

        using var ro = db.BeginReadTransaction();
        var g = ro.Query;

        var typed = g.Vertices<Person>().Has(p => p.Name, "A")
            .MeetingAsOrganizer().Venue().ToListWithIds();
        var untyped = g.Vertex(a)
            .Nexuses("Meeting", "Organizer").Members("Venue").ToList();

        Ids(typed).Should().BeEquivalentTo(Ids(untyped));
        Ids(typed).Should().BeEquivalentTo(Ids(new List<VertexId> { hq }));
    }

    [Fact]
    public void Same_vertex_type_in_multiple_roles_matches_untyped()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId alice, bob;
        using (var tx = db.BeginWriteTransaction())
        {
            alice = Person.Insert(tx, new Person { Name = "Alice" });
            bob = Person.Insert(tx, new Person { Name = "Bob" });
            // Subject と Object は同じVertex型 (Person) の別ロール。逆向きの 1 件も
            // 加え、ロール指定が方向を正しく区別することを確認する。
            Statement.Insert(tx, new Statement { Subject = alice, Object = bob, Predicate = "knows" });
            Statement.Insert(tx, new Statement { Subject = bob, Object = alice, Predicate = "employs" });
            tx.Commit();
        }

        using var ro = db.BeginReadTransaction();
        var g = ro.Query;

        // Subject ロールで参加する Statement の Object メンバー。
        var typedObjects = g.Vertices<Person>().Has(p => p.Name, "Alice")
            .StatementAsSubject().Object().ToListWithIds();
        var untypedObjects = g.Vertex(alice)
            .Nexuses("Statement", "Subject").Members("Object").ToList();

        Ids(typedObjects).Should().BeEquivalentTo(Ids(untypedObjects));
        Ids(typedObjects).Should().BeEquivalentTo(Ids(new List<VertexId> { bob }));

        // Object ロールで参加する Statement の Subject メンバー (逆方向)。
        var typedSubjects = g.Vertices<Person>().Has(p => p.Name, "Alice")
            .StatementAsObject().Subject().ToListWithIds();
        var untypedSubjects = g.Vertex(alice)
            .Nexuses("Statement", "Object").Members("Subject").ToList();

        Ids(typedSubjects).Should().BeEquivalentTo(Ids(untypedSubjects));
        Ids(typedSubjects).Should().BeEquivalentTo(Ids(new List<VertexId> { bob }));
    }

    [Fact]
    public void Nexus_property_filter_matches_untyped()
    {
        using var db = QuiverDatabase.Open(_path);

        VertexId alice, acme, globex;
        using (var tx = db.BeginWriteTransaction())
        {
            alice = Person.Insert(tx, new Person { Name = "Alice" });
            acme = Company.Insert(tx, new Company { Name = "Acme" });
            globex = Company.Insert(tx, new Company { Name = "Globex" });
            Employment.Insert(tx, new Employment
            {
                Employee = alice, Employer = acme, Title = "Engineer", Year = 2020,
            });
            Employment.Insert(tx, new Employment
            {
                Employee = alice, Employer = globex, Title = "Manager", Year = 2022,
            });
            tx.Commit();
        }

        using var ro = db.BeginReadTransaction();
        var g = ro.Query;

        // Nexusプロパティの式ツリーフィルタは型なし Has(key, value) と同じ述語になる。
        var typed = g.Vertices<Person>().Has(p => p.Name, "Alice")
            .EmploymentAsEmployee().Has(e => e.Title, "Engineer").Employer().ToListWithIds();
        var untyped = g.Vertex(alice)
            .Nexuses("Employment", "Employee").Has("Title", "Engineer").Members("Employer").ToList();

        Ids(typed).Should().BeEquivalentTo(Ids(untyped));
        Ids(typed).Should().BeEquivalentTo(Ids(new List<VertexId> { acme }));

        // 型付きNexus終端はエンティティを復元できる。
        var employments = g.Vertices<Person>().Has(p => p.Name, "Alice")
            .EmploymentAsEmployee().Has(e => e.Year, 2022).ToList();
        employments.Should().ContainSingle().Which.Title.Should().Be("Manager");
    }
}

// Subject と Object が同じVertex型 (Person) を束縛するNexus。
// 同一Vertex型の複数ロールをロール指定で区別できることの検証に使う。
[Nexus("Statement")]
internal partial class Statement
{
    [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
    [Role("Object")] public GraphVertexRef<Person> Object { get; set; }
    [Property] public string Predicate { get; set; } = "";
}
