using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Quiver.SourceGen;

namespace Quiver.SourceGen.Tests;

public class GraphNodeGeneratorTests
{
    [Fact]
    public void Generator_runs_without_exception_on_empty_compilation()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new GraphNodeGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Generator_emits_partial_class_for_GraphNode()
    {
        var attributeRef = typeof(Quiver.Client.GraphNodeAttribute).Assembly.Location;
        var engineRef    = typeof(Quiver.IGraphTransaction).Assembly.Location;

        var source = """
            using Quiver.Client;
            namespace MyApp;

            [GraphNode("Person")]
            public partial class Person
            {
                [GraphProperty]
                public string Name { get; set; } = "";

                [GraphProperty]
                public int Age { get; set; }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(attributeRef),
            MetadataReference.CreateFromFile(engineRef),
        };

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new GraphNodeGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult();
        var generated = result.GeneratedTrees;
        generated.Should().NotBeEmpty("generator should emit code for [GraphNode] class");

        var generatedSource = generated[0].ToString();
        generatedSource.Should().Contain("public static string GraphLabel => \"Person\"");
        generatedSource.Should().Contain("public static Quiver.Core.NodeId Insert(");
        generatedSource.Should().Contain("public static Person Load(");
        generatedSource.Should().Contain("public static void Update(");
        generatedSource.Should().Contain("public static void Delete(");
    }
}

public class GraphRelationshipGeneratorTests
{
    [Fact]
    public void Generator_runs_without_exception_on_empty_compilation()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new GraphRelationshipGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        driver.GetRunResult().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Generator_emits_partial_class_for_GraphRelationship()
    {
        var attributeRef = typeof(Quiver.Client.GraphRelationshipAttribute).Assembly.Location;
        var engineRef    = typeof(Quiver.IGraphTransaction).Assembly.Location;

        var source = """
            using Quiver.Client;
            namespace MyApp;

            [GraphRelationship("KNOWS")]
            public partial class Knows
            {
                [GraphProperty]
                public int Since { get; set; }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(attributeRef),
            MetadataReference.CreateFromFile(engineRef),
        };

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new GraphRelationshipGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult();
        result.GeneratedTrees.Should().NotBeEmpty("generator should emit code for [GraphRelationship] class");

        var generatedSource = result.GeneratedTrees[0].ToString();
        generatedSource.Should().Contain("public static string GraphType => \"KNOWS\"");
        generatedSource.Should().Contain("public static Quiver.Core.RelationshipId Insert(");
        generatedSource.Should().Contain("tx.CreateRelationship(from, to, \"KNOWS\")");
        generatedSource.Should().Contain("public static Knows Load(");
        generatedSource.Should().Contain("public static void Update(");
        generatedSource.Should().Contain("public static void Delete(");
    }
}
