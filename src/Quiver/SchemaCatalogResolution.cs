using Quiver.Core;

namespace Quiver;

internal static class SchemaCatalogResolution
{
    internal static LabelId ResolveLabel(this ISchemaCatalog schema, string name)
        => schema.TryGetLabelId(name, out var id) ? id : LabelId.Invalid;

    internal static EdgeTypeId ResolveEdgeType(this ISchemaCatalog schema, string name)
        => schema.TryGetEdgeTypeId(name, out var id) ? id : EdgeTypeId.Invalid;

    internal static PropertyKeyId ResolvePropertyKey(this ISchemaCatalog schema, string name)
        => schema.TryGetPropertyKeyId(name, out var id) ? id : PropertyKeyId.Invalid;
}
