using System;

namespace GraphDb.Engine.Client;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GraphRelationshipAttribute : Attribute
{
    public GraphRelationshipAttribute(string? type = null) { Type = type; }
    public string? Type { get; }
}
