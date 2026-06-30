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
        var attributeRef = typeof(Quiver.Api.NodeAttribute).Assembly.Location;
        var engineRef    = typeof(Quiver.IGraphTransaction).Assembly.Location;

        var source = """
            using Quiver.Api;
            namespace MyApp;

            [Node("Person")]
            public partial class Person
            {
                [Property]
                public string Name { get; set; } = "";

                [Property]
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
        generated.Should().NotBeEmpty("generator should emit code for [Node] class");

        var generatedSource = generated[0].ToString();
        generatedSource.Should().Contain("public static string GraphLabel => \"Person\"");
        generatedSource.Should().Contain("public static Quiver.Core.NodeId Insert(");
        generatedSource.Should().Contain("public static Person Load(");
        generatedSource.Should().Contain("public static void Update(");
        generatedSource.Should().Contain("public static void Delete(");
    }

    [Fact]
    public void Generator_emits_FloatArray_property_accessors()
    {
        var attributeRef = typeof(Quiver.Api.NodeAttribute).Assembly.Location;
        var engineRef    = typeof(Quiver.IGraphTransaction).Assembly.Location;

        var source = """
            using Quiver.Api;
            namespace MyApp;

            [Node("Sensor")]
            public partial class Sensor
            {
                [Property]
                public string Site { get; set; } = "";

                [Property]
                public float[] Waveform { get; set; }
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
        generated.Should().NotBeEmpty();

        var generatedSource = generated[0].ToString();
        generatedSource.Should().Contain("PropertyValue.FromFloatArray(entity.Waveform)");
        generatedSource.Should().Contain("FloatArrayValue.ToArray()");
    }

    [Fact]
    public void Generator_emits_MultiValue_List_property()
    {
        var attributeRef = typeof(Quiver.Api.NodeAttribute).Assembly.Location;
        var engineRef    = typeof(Quiver.IGraphTransaction).Assembly.Location;

        var source = """
            using System.Collections.Generic;
            using Quiver.Api;
            namespace MyApp;

            [Node("Sensor")]
            public partial class Sensor
            {
                [Property]
                public string Site { get; set; } = "";

                [Property]
                public List<string> Tags { get; set; } = new();
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
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
        generated.Should().NotBeEmpty();

        var src = generated[0].ToString();
        // Insert: foreach + AddPropertyValue で追加
        src.Should().Contain("tx.AddPropertyValue(id, \"Tags\", PropertyValue.FromString(__v))");
        // Load: GetPropertyValues + 収集
        src.Should().Contain("tx.GetPropertyValues(id, \"Tags\")");
        src.Should().Contain("List<string>");
        // Update: 差分ベースの RemovePropertyValue
        src.Should().Contain("tx.RemovePropertyValue(id, \"Tags\",");
        // scalar property は引き続き SetProperty を使う
        src.Should().Contain("tx.SetProperty(id, \"Site\",");
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
        var attributeRef = typeof(Quiver.Api.RelationshipAttribute<,>).Assembly.Location;
        var engineRef    = typeof(Quiver.IGraphTransaction).Assembly.Location;

        var source = """
            using Quiver.Api;
            namespace MyApp;

            [Node("Person")]
            public partial class Person
            {
                [Property]
                public string Name { get; set; } = "";
            }

            [Relationship<Person, Person>("KNOWS")]
            public partial class Knows
            {
                [Property]
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
        result.GeneratedTrees.Should().NotBeEmpty("generator should emit code for [Relationship] class");

        var generatedSource = result.GeneratedTrees[0].ToString();
        generatedSource.Should().Contain("public static string GraphType => \"KNOWS\"");
        generatedSource.Should().Contain("public static Quiver.Core.RelationshipId Insert(");
        generatedSource.Should().Contain("tx.CreateRelationship(from, to, \"KNOWS\")");
        generatedSource.Should().Contain("public static Knows Load(");
        generatedSource.Should().Contain("public static void Update(");
        generatedSource.Should().Contain("public static void Delete(");
        // write sink の糖衣構文
        generatedSource.Should().Contain("public static long AddKnows(");
        generatedSource.Should().Contain("public static (long Created, long Matched) MergeKnows(");
    }
}
