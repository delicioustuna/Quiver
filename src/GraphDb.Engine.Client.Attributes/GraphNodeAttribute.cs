using System;

namespace GraphDb.Engine.Client;

[AttributeUsage(AttributeTargets.Class)]
public sealed class GraphNodeAttribute : Attribute
{
    public GraphNodeAttribute(string? label = null) { Label = label; }
    public string? Label { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class GraphPropertyAttribute : Attribute
{
    public GraphPropertyAttribute(string? key = null) { Key = key; }
    public string? Key { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class GraphIndexedAttribute : Attribute
{
    public GraphIndexedAttribute(string? indexName = null) { IndexName = indexName; }
    public string? IndexName { get; }
}
