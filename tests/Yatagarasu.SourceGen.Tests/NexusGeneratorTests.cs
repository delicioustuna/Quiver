using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Yatagarasu.SourceGen;

namespace Yatagarasu.SourceGen.Tests;

public class GraphNexusGeneratorTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        // ランタイム TPA 一式に Yatagarasu 本体を足すことで、生成コードの完全コンパイル
        // (制約 where TVertex : IGraphVertex<TVertex> の解決を含む) を検証できる。
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var yatagarasu = MetadataReference.CreateFromFile(typeof(Yatagarasu.IWriteTransaction).Assembly.Location);
        return tpa.Append(yatagarasu).ToArray();
    }

    private static (string generated, ImmutableArrayLike diagnostics, CSharpCompilation compilation) Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [tree],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // vertex と nexus の両生成器を同時に走らせる。where TVertex : IGraphVertex<TVertex>
        // 制約は両者の出力が合流した最終コンパイルで解決される契約のため。
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new GraphVertexGenerator(), new GraphNexusGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var result = driver.GetRunResult();
        var generated = result.Results
            .SelectMany(r => r.GeneratedSources)
            .FirstOrDefault(s => s.HintName.Contains("GraphNexus"))
            .SourceText?.ToString() ?? "";
        // nexus 生成器が報告した診断のみを対象にする。
        var diagnostics = result.Diagnostics
            .Where(d => d.Id.StartsWith("QVRHE", System.StringComparison.Ordinal))
            .ToArray();
        return (generated, new ImmutableArrayLike(diagnostics), (CSharpCompilation)outputCompilation);
    }

    /// <summary>生成コードとユーザーコードを合流させて完全コンパイルし、エラー診断を返す。</summary>
    private static Diagnostic[] CompileErrors(CSharpCompilation compilation) =>
        compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

    // using は必ず型宣言より前に置く (usings-after-types はコンパイルエラーとなり、
    // 属性コンストラクタの束縛が壊れてロール名などの引数が読めなくなる)。
    private const string Header = """
        using System.Collections.Generic;
        using Yatagarasu;
        using Yatagarasu.Api;
        """;

    private const string VertexStub = """
        [Vertex("Person")] public partial class Person { }
        [Vertex("Place")]  public partial class Place { }
        """;

    [Fact]
    public void Runs_without_exception_on_empty_compilation()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new GraphNexusGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        driver.GetRunResult().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Emits_partial_class_for_single_role_nexus()
    {
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphVertexRef<Person> Object { get; set; }
                [Property] public string Predicate { get; set; } = "";
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("public static string GraphType => \"Fact\"");
        generated.Should().Contain("public static Yatagarasu.Core.NexusId Insert(");
        generated.Should().Contain("tx.CreateNexus(\"Fact\"");
        generated.Should().Contain("new Yatagarasu.NexusMember(\"Subject\", entity.Subject.VertexId)");
        generated.Should().Contain("public static Fact Load(");
        generated.Should().Contain("tx.GetMembers(id, \"Subject\")");
        generated.Should().Contain("tx.SetProperty(id, \"Predicate\",");
        generated.Should().Contain("public static Yatagarasu.NexusReplacement Replace(");
        generated.Should().Contain("tx.ReplaceNexus(id, \"Fact\"");
        generated.Should().Contain("Update(tx, __replacement.NewId, entity)");
        generated.Should().Contain("public static void Delete(");
        // Insert は CreateNexus を一度だけ呼ぶ。
        CountOccurrences(generated, "tx.CreateNexus(").Should().Be(1);
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Emits_multi_role_via_IReadOnlyList()
    {
        var source = Header + VertexStub + """

            [Nexus("Meeting")]
            public partial class Meeting
            {
                [Role("Attendee")] public IReadOnlyList<GraphVertexRef<Person>> Attendees { get; set; } = new List<GraphVertexRef<Person>>();
                [Role("Host")]     public GraphVertexRef<Person> Host { get; set; }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("foreach (var __r in entity.Attendees)");
        generated.Should().Contain("new Yatagarasu.NexusMember(\"Attendee\", __r.VertexId)");
        generated.Should().Contain("var __list = new System.Collections.Generic.List<Yatagarasu.Api.GraphVertexRef<global::Person>>()");
        CountOccurrences(generated, "tx.CreateNexus(").Should().Be(1);
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Emits_nullable_optional_single_role()
    {
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")]  public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]   public GraphVertexRef<Person> Object { get; set; }
                [Role("Location")] public GraphVertexRef<Place>? Location { get; set; }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        // optional role は値がある場合のみメンバーを追加する。
        generated.Should().Contain("if (entity.Location is { } __Location)");
        generated.Should().Contain("new Yatagarasu.NexusMember(\"Location\", __Location.VertexId)");
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Emits_for_class_in_namespace()
    {
        var source = """
            using Yatagarasu.Api;
            namespace My.Graph
            {
                [Vertex("Person")] public partial class Person { }
                [Nexus("Fact")]
                public partial class Fact
                {
                    [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                    [Role("Object")]  public GraphVertexRef<Person> Object { get; set; }
                }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("namespace My.Graph;");
        generated.Should().Contain("Yatagarasu.Api.GraphVertexRef<global::My.Graph.Person>");
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Reports_diagnostic_when_role_and_property_on_same_member()
    {
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphVertexRef<Person> Object { get; set; }
                [Role("Bad")] [Property] public GraphVertexRef<Person> Bad { get; set; }
            }
            """;

        var (_, diagnostics, _) = Run(source);

        diagnostics.Value.Should().Contain(d => d.Id == "QVRHE001");
    }

    [Fact]
    public void Reports_diagnostic_when_role_is_not_vertex_ref()
    {
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]  public string Object { get; set; } = "";
            }
            """;

        var (_, diagnostics, _) = Run(source);

        diagnostics.Value.Should().Contain(d => d.Id == "QVRHE002");
    }

    [Fact]
    public void Reports_diagnostic_when_member_has_no_setter()
    {
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphVertexRef<Person> Object { get; } = default;
            }
            """;

        var (_, diagnostics, _) = Run(source);

        diagnostics.Value.Should().Contain(d => d.Id == "QVRHE003");
    }

    // ── 型保存トラバーサル糖衣 ───────────────────────────────────────────────

    [Fact]
    public void Emits_typed_traversal_extensions_per_role()
    {
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                [Role("Where")]   public GraphVertexRef<Place> Location { get; set; }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("public static class FactTraversalExtensions");
        // vertex → nexus: ロール名リテラルを畳み込み、型なし DSL と同じ経路へ委譲する。
        generated.Should().Contain(
            "public static Yatagarasu.Api.TypedGraphNexusTraversal<Fact> FactAsSubject(this Yatagarasu.Api.TypedGraphTraversal<global::Person> source)");
        generated.Should().Contain("source.Nexuses<Fact>(\"Subject\")");
        // nexus → member: ロールプロパティ名がメソッド名、[Role] の名がロール文字列。
        generated.Should().Contain(
            "public static Yatagarasu.Api.TypedGraphTraversal<global::Place> Location(this Yatagarasu.Api.TypedGraphNexusTraversal<Fact> source)");
        generated.Should().Contain("source.MembersOf<global::Place>(\"Where\")");
        // co-membership: 起点Vertex除外版。
        generated.Should().Contain(
            "public static Yatagarasu.Api.TypedGraphTraversal<global::Person> OtherSubject(this Yatagarasu.Api.TypedGraphNexusTraversal<Fact> source)");
        generated.Should().Contain("source.OtherMembersOf<global::Person>(\"Subject\")");
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Typed_workspace_call_sites_compile()
    {
        // 単一・複数・nullable 省略可能ロールを含む Nexus が、公開 workspace
        // 境界から型を保ったまま追加・復元できることを検証する。
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")]  public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]   public GraphVertexRef<Person> Object { get; set; }
                [Role("Attendee")] public IReadOnlyList<GraphVertexRef<Person>> Attendees { get; set; } = new List<GraphVertexRef<Person>>();
                [Role("Where")]    public GraphVertexRef<Place>? Location { get; set; }
                [Property] public string Predicate { get; set; } = "";
            }

            public static class CallSites
            {
                public static GraphNexusEntity<Fact> Add(
                    TypedGraphWriteScope write,
                    GraphEntity<Person> subject,
                    GraphEntity<Person> @object,
                    GraphEntity<Place> place)
                {
                    return write.Add(new Fact
                    {
                        Subject = subject,
                        Object = @object,
                        Attendees = new[] { (GraphVertexRef<Person>)subject, @object },
                        Location = place,
                        Predicate = "born-in",
                    });
                }

                public static Fact Get(TypedGraphReadScope read, GraphNexusEntity<Fact> fact) => read.Get(fact);
            }
            """;

        var (_, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Wrong_vertex_type_role_assignment_is_compile_error()
    {
        // Subject ロールは Person に束縛されているため、Place の参照を代入する
        // コードはコンパイルエラーになる (実行時エラーにしない)。
        var source = Header + VertexStub + """

            [Nexus("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphVertexRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphVertexRef<Person> Object { get; set; }
            }

            public static class CallSites
            {
                public static Fact Wrong(GraphEntity<Place> place)
                {
                    return new Fact { Subject = place, Object = place };
                }
            }
            """;

        var (_, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        CompileErrors(compilation).Should().Contain(d => d.Id == "CS0029");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>診断配列を LINQ しやすくする薄いラッパ。</summary>
    public readonly struct ImmutableArrayLike
    {
        public ImmutableArrayLike(Diagnostic[] value) => Value = value;
        public Diagnostic[] Value { get; }
    }
}
