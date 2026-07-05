using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Quiver.SourceGen;

namespace Quiver.SourceGen.Tests;

public class GraphHyperedgeGeneratorTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        // ランタイム TPA 一式に Quiver 本体を足すことで、生成コードの完全コンパイル
        // (制約 where TNode : IGraphNode<TNode> の解決を含む) を検証できる。
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var quiver = MetadataReference.CreateFromFile(typeof(Quiver.IGraphTransaction).Assembly.Location);
        return tpa.Append(quiver).ToArray();
    }

    private static (string generated, ImmutableArrayLike diagnostics, CSharpCompilation compilation) Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [tree],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // node と hyperedge の両生成器を同時に走らせる。where TNode : IGraphNode<TNode>
        // 制約は両者の出力が合流した最終コンパイルで解決される契約のため。
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new GraphNodeGenerator(), new GraphHyperedgeGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var result = driver.GetRunResult();
        var generated = result.Results
            .SelectMany(r => r.GeneratedSources)
            .FirstOrDefault(s => s.HintName.Contains("GraphHyperedge"))
            .SourceText?.ToString() ?? "";
        // hyperedge 生成器が報告した診断のみを対象にする。
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
        using Quiver.Api;
        """;

    private const string NodeStub = """
        [Node("Person")] public partial class Person { }
        [Node("Place")]  public partial class Place { }
        """;

    [Fact]
    public void Runs_without_exception_on_empty_compilation()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new GraphHyperedgeGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        driver.GetRunResult().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Emits_partial_class_for_single_role_hyperedge()
    {
        var source = Header + NodeStub + """

            [Hyperedge("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphNodeRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphNodeRef<Person> Object { get; set; }
                [Property] public string Predicate { get; set; } = "";
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("public static string GraphType => \"Fact\"");
        generated.Should().Contain("public static Quiver.Core.HyperedgeId Insert(");
        generated.Should().Contain("tx.CreateHyperedge(\"Fact\"");
        generated.Should().Contain("new Quiver.HyperedgeMember(\"Subject\", entity.Subject.NodeId)");
        generated.Should().Contain("public static Fact Load(");
        generated.Should().Contain("tx.GetMembers(id, \"Subject\")");
        generated.Should().Contain("tx.SetProperty(id, \"Predicate\",");
        generated.Should().Contain("public static void Delete(");
        // Insert は CreateHyperedge を一度だけ呼ぶ。
        CountOccurrences(generated, "tx.CreateHyperedge(").Should().Be(1);
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Emits_multi_role_via_IReadOnlyList()
    {
        var source = Header + NodeStub + """

            [Hyperedge("Meeting")]
            public partial class Meeting
            {
                [Role("Attendee")] public IReadOnlyList<GraphNodeRef<Person>> Attendees { get; set; } = new List<GraphNodeRef<Person>>();
                [Role("Host")]     public GraphNodeRef<Person> Host { get; set; }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("foreach (var __r in entity.Attendees)");
        generated.Should().Contain("new Quiver.HyperedgeMember(\"Attendee\", __r.NodeId)");
        generated.Should().Contain("var __list = new System.Collections.Generic.List<Quiver.Api.GraphNodeRef<global::Person>>()");
        CountOccurrences(generated, "tx.CreateHyperedge(").Should().Be(1);
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Emits_nullable_optional_single_role()
    {
        var source = Header + NodeStub + """

            [Hyperedge("Fact")]
            public partial class Fact
            {
                [Role("Subject")]  public GraphNodeRef<Person> Subject { get; set; }
                [Role("Object")]   public GraphNodeRef<Person> Object { get; set; }
                [Role("Location")] public GraphNodeRef<Place>? Location { get; set; }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        // optional role は値がある場合のみメンバーを追加する。
        generated.Should().Contain("if (entity.Location is { } __Location)");
        generated.Should().Contain("new Quiver.HyperedgeMember(\"Location\", __Location.NodeId)");
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Emits_for_class_in_namespace()
    {
        var source = """
            using Quiver.Api;
            namespace My.Graph
            {
                [Node("Person")] public partial class Person { }
                [Hyperedge("Fact")]
                public partial class Fact
                {
                    [Role("Subject")] public GraphNodeRef<Person> Subject { get; set; }
                    [Role("Object")]  public GraphNodeRef<Person> Object { get; set; }
                }
            }
            """;

        var (generated, diagnostics, compilation) = Run(source);

        diagnostics.Value.Should().BeEmpty();
        generated.Should().Contain("namespace My.Graph;");
        generated.Should().Contain("Quiver.Api.GraphNodeRef<global::My.Graph.Person>");
        CompileErrors(compilation).Should().BeEmpty();
    }

    [Fact]
    public void Reports_diagnostic_when_role_and_property_on_same_member()
    {
        var source = Header + NodeStub + """

            [Hyperedge("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphNodeRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphNodeRef<Person> Object { get; set; }
                [Role("Bad")] [Property] public GraphNodeRef<Person> Bad { get; set; }
            }
            """;

        var (_, diagnostics, _) = Run(source);

        diagnostics.Value.Should().Contain(d => d.Id == "QVRHE001");
    }

    [Fact]
    public void Reports_diagnostic_when_role_is_not_node_ref()
    {
        var source = Header + NodeStub + """

            [Hyperedge("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphNodeRef<Person> Subject { get; set; }
                [Role("Object")]  public string Object { get; set; } = "";
            }
            """;

        var (_, diagnostics, _) = Run(source);

        diagnostics.Value.Should().Contain(d => d.Id == "QVRHE002");
    }

    [Fact]
    public void Reports_diagnostic_when_member_has_no_setter()
    {
        var source = Header + NodeStub + """

            [Hyperedge("Fact")]
            public partial class Fact
            {
                [Role("Subject")] public GraphNodeRef<Person> Subject { get; set; }
                [Role("Object")]  public GraphNodeRef<Person> Object { get; } = default;
            }
            """;

        var (_, diagnostics, _) = Run(source);

        diagnostics.Value.Should().Contain(d => d.Id == "QVRHE003");
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
