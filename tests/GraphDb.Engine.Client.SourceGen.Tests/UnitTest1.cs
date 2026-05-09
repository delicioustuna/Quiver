using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using GraphDb.Engine.Client.SourceGen;

namespace GraphDb.Engine.Client.SourceGen.Tests;

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
        var attributeRef = typeof(GraphDb.Engine.Client.GraphNodeAttribute).Assembly.Location;
        var engineRef    = typeof(GraphDb.Engine.IGraphTransaction).Assembly.Location;

        var source = """
            using GraphDb.Engine.Client;
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
        generatedSource.Should().Contain("public static GraphDb.Engine.Core.NodeId Insert(");
        generatedSource.Should().Contain("public static Person Load(");
        generatedSource.Should().Contain("public static void Update(");
        generatedSource.Should().Contain("public static void Delete(");
    }
}
