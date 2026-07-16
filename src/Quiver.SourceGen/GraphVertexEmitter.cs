using System.Text;

namespace Quiver.SourceGen;

internal static class GraphVertexEmitter
{
    private static readonly Dictionary<string, (string write, string read)> _typeMap = new()
    {
        ["string"]  = ("PropertyValue.FromString(entity.{0})", "System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, \"{1}\").Utf8StringValue)"),
        ["string?"] = ("PropertyValue.FromString(entity.{0} ?? \"\")", "System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, \"{1}\").Utf8StringValue)"),
        ["int"]     = ("PropertyValue.FromInt32(entity.{0})", "tx.GetProperty(id, \"{1}\").Int32Value"),
        ["long"]    = ("PropertyValue.FromInt64(entity.{0})", "tx.GetProperty(id, \"{1}\").Int64Value"),
        ["double"]  = ("PropertyValue.FromDouble(entity.{0})", "tx.GetProperty(id, \"{1}\").DoubleValue"),
        ["float"]   = ("PropertyValue.FromDouble((double)entity.{0})", "(float)tx.GetProperty(id, \"{1}\").DoubleValue"),
        ["Half"]    = ("PropertyValue.FromDouble((double)entity.{0})", "(System.Half)tx.GetProperty(id, \"{1}\").DoubleValue"),
        ["bool"]    = ("PropertyValue.FromBool(entity.{0})", "tx.GetProperty(id, \"{1}\").BoolValue"),
        ["DateTime"]       = ("PropertyValue.FromDateTime(entity.{0})", "tx.GetProperty(id, \"{1}\").DateTimeValue"),
        ["DateTimeOffset"] = ("PropertyValue.FromDateTimeOffset(entity.{0})", "tx.GetProperty(id, \"{1}\").DateTimeOffsetValue"),
        ["DateOnly"]       = ("PropertyValue.FromDateOnly(entity.{0})", "tx.GetProperty(id, \"{1}\").DateOnlyValue"),
        ["TimeOnly"]       = ("PropertyValue.FromTimeOnly(entity.{0})", "tx.GetProperty(id, \"{1}\").TimeOnlyValue"),
        ["TimeSpan"]       = ("PropertyValue.FromTimeSpan(entity.{0})", "tx.GetProperty(id, \"{1}\").TimeSpanValue"),
        ["float[]"]        = ("PropertyValue.FromFloatArray(entity.{0})", "tx.GetProperty(id, \"{1}\").FloatArrayValue.ToArray()"),
    };

    // 複数値: 要素型ごとの PropertyValue.From*(loopVar)
    private static readonly Dictionary<string, string> _mvWriteExpr = new()
    {
        ["string"]  = "PropertyValue.FromString(__v)",
        ["int"]     = "PropertyValue.FromInt32(__v)",
        ["long"]    = "PropertyValue.FromInt64(__v)",
        ["double"]  = "PropertyValue.FromDouble(__v)",
        ["float"]   = "PropertyValue.FromDouble((double)__v)",
        ["Half"]    = "PropertyValue.FromDouble((double)__v)",
        ["bool"]    = "PropertyValue.FromBool(__v)",
        ["DateTime"]       = "PropertyValue.FromDateTime(__v)",
        ["DateTimeOffset"] = "PropertyValue.FromDateTimeOffset(__v)",
        ["DateOnly"]       = "PropertyValue.FromDateOnly(__v)",
        ["TimeOnly"]       = "PropertyValue.FromTimeOnly(__v)",
        ["TimeSpan"]       = "PropertyValue.FromTimeSpan(__v)",
    };

    // 複数値: enumerator.Current から CLR 型への変換
    private static readonly Dictionary<string, string> _mvReadExpr = new()
    {
        ["string"]  = "System.Text.Encoding.UTF8.GetString(__e.Current.Utf8StringValue)",
        ["int"]     = "__e.Current.Int32Value",
        ["long"]    = "__e.Current.Int64Value",
        ["double"]  = "__e.Current.DoubleValue",
        ["float"]   = "(float)__e.Current.DoubleValue",
        ["Half"]    = "(System.Half)__e.Current.DoubleValue",
        ["bool"]    = "__e.Current.BoolValue",
        ["DateTime"]       = "__e.Current.DateTimeValue",
        ["DateTimeOffset"] = "__e.Current.DateTimeOffsetValue",
        ["DateOnly"]       = "__e.Current.DateOnlyValue",
        ["TimeOnly"]       = "__e.Current.TimeOnlyValue",
        ["TimeSpan"]       = "__e.Current.TimeSpanValue",
    };

    private static readonly Dictionary<string, string> _indexCallMap = new()
    {
        ["string"]  = "tx.IndexInsert(\"{0}\", entity.{1}, id);",
        ["string?"] = "tx.IndexInsert(\"{0}\", entity.{1} ?? \"\", id);",
        ["int"]     = "tx.IndexInsert(\"{0}\", (long)entity.{1}, id);",
        ["long"]    = "tx.IndexInsert(\"{0}\", entity.{1}, id);",
        ["double"]  = "tx.IndexInsert(\"{0}\", entity.{1}, id);",
    };

    private static readonly Dictionary<string, string> _seekCallMap = new()
    {
        ["string"]  = "Quiver.Storage.Records.PropertyValue.FromString(value)",
        ["string?"] = "Quiver.Storage.Records.PropertyValue.FromString(value ?? \"\")",
        ["int"]     = "Quiver.Storage.Records.PropertyValue.FromInt64((long)value)",
        ["long"]    = "Quiver.Storage.Records.PropertyValue.FromInt64(value)",
        ["double"]  = "Quiver.Storage.Records.PropertyValue.FromDouble(value)",
    };

    private static readonly Dictionary<string, string> _indexKindMap = new()
    {
        ["string"]  = "Quiver.IndexKind.StringEquality",
        ["string?"] = "Quiver.IndexKind.StringEquality",
        ["int"]     = "Quiver.IndexKind.Int32Equality",
        ["long"]    = "Quiver.IndexKind.Int64Equality",
        ["double"]  = "Quiver.IndexKind.DoubleEquality",
    };

    public static string Emit(GraphVertexModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Quiver;");
        sb.AppendLine("using Quiver.Api;");
        sb.AppendLine("using Quiver.Core;");
        sb.AppendLine("using Quiver.Storage.Records;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.AppendLine($"namespace {model.Namespace};");
            sb.AppendLine();
        }

        var indexedProps = model.Properties.FindAll(p => p.IndexName != null);
        var multiValueProps = model.Properties.FindAll(p => p.IsMultiValued);

        sb.AppendLine($"partial class {model.ClassName} : Quiver.Api.IGraphVertex<{model.ClassName}>, Quiver.IGraphVertexSchema<{model.ClassName}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public static string GraphLabel => \"{model.Label}\";");
        sb.AppendLine();

        // 挿入
        sb.AppendLine($"    public static Quiver.Core.VertexId Insert(IGraphTransaction tx, {model.ClassName} entity)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var id = tx.CreateVertex(\"{model.Label}\");");
        foreach (var prop in model.Properties)
        {
            if (prop.IsMultiValued)
                EmitInsertMultiValue(sb, prop);
            else
                EmitSetProperty(sb, prop);
        }
        sb.AppendLine("        return id;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // インデックス付き挿入
        sb.AppendLine($"    public static Quiver.Core.VertexId InsertIndexed(IGraphTransaction tx, {model.ClassName} entity)");
        sb.AppendLine("    {");
        sb.AppendLine("        var id = Insert(tx, entity);");
        foreach (var prop in indexedProps)
        {
            if (_indexCallMap.TryGetValue(prop.CSharpType, out var fmt))
                sb.AppendLine($"        {string.Format(fmt, prop.IndexName, prop.PropertyName)}");
        }
        sb.AppendLine("        return id;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // 読み込み
        sb.AppendLine($"    public static {model.ClassName} Load(IGraphTransaction tx, Quiver.Core.VertexId id)");
        sb.AppendLine("    {");
        if (multiValueProps.Count > 0)
        {
            sb.AppendLine($"        var __entity = new {model.ClassName}");
            sb.AppendLine("        {");
            foreach (var prop in model.Properties)
            {
                if (!prop.IsMultiValued && _typeMap.TryGetValue(prop.CSharpType, out var map))
                    sb.AppendLine($"            {prop.PropertyName} = {string.Format(map.read, prop.PropertyName, prop.GraphKey)},");
            }
            sb.AppendLine("        };");
            foreach (var prop in multiValueProps)
                EmitLoadMultiValue(sb, prop);
            sb.AppendLine("        return __entity;");
        }
        else
        {
            sb.AppendLine($"        return new {model.ClassName}");
            sb.AppendLine("        {");
            foreach (var prop in model.Properties)
            {
                if (_typeMap.TryGetValue(prop.CSharpType, out var map))
                    sb.AppendLine($"            {prop.PropertyName} = {string.Format(map.read, prop.PropertyName, prop.GraphKey)},");
            }
            sb.AppendLine("        };");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // 更新
        sb.AppendLine($"    public static void Update(IGraphTransaction tx, Quiver.Core.VertexId id, {model.ClassName} entity)");
        sb.AppendLine("    {");
        foreach (var prop in model.Properties)
        {
            if (prop.IsMultiValued)
                EmitUpdateMultiValue(sb, prop);
            else
                EmitSetProperty(sb, prop);
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // 削除
        sb.AppendLine($"    public static void Delete(IGraphTransaction tx, Quiver.Core.VertexId id) => tx.DeleteVertex(id);");

        // 全インデックスの作成保証
        sb.AppendLine();
        sb.AppendLine("    public static void EnsureIndexes(Quiver.ISchemaApi schema)");
        sb.AppendLine("    {");
        foreach (var prop in indexedProps)
        {
            if (!_indexKindMap.TryGetValue(prop.CSharpType, out var kindExpr)) continue;
            sb.AppendLine(
                $"        schema.CreateIndex(\"{prop.IndexName}\", \"{model.Label}\", \"{prop.GraphKey}\", {kindExpr});");
        }
        sb.AppendLine("    }");

        // 単一プロパティのインデックス作成保証
        sb.AppendLine();
        sb.AppendLine("    public static void EnsureIndex(Quiver.ISchemaApi schema, string propertyName, Quiver.IndexKind? kindOverride)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (propertyName)");
        sb.AppendLine("        {");
        foreach (var prop in indexedProps)
        {
            if (!_indexKindMap.TryGetValue(prop.CSharpType, out var kindExpr)) continue;
            sb.AppendLine($"            case \"{prop.PropertyName}\":");
            sb.AppendLine(
                $"                schema.CreateIndex(\"{prop.IndexName}\", \"{model.Label}\", \"{prop.GraphKey}\", kindOverride ?? {kindExpr});");
            sb.AppendLine("                return;");
        }
        sb.AppendLine("            default:");
        sb.AppendLine($"                throw new System.ArgumentException(\"'\" + propertyName + \"' は {model.ClassName} で [Indexed] が付与されたプロパティではありません。\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        // FindBy* 検索
        foreach (var prop in indexedProps)
        {
            if (!_seekCallMap.TryGetValue(prop.CSharpType, out var seekExpr)) continue;
            var paramType = prop.CSharpType.TrimEnd('?');
            sb.AppendLine();
            sb.AppendLine($"    public static System.Collections.Generic.List<(Quiver.Core.VertexId Id, {model.ClassName} Entity)> FindBy{prop.PropertyName}(");
            sb.AppendLine($"        IGraphTransaction tx, {paramType} value)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var results = new System.Collections.Generic.List<(Quiver.Core.VertexId, {model.ClassName})>();");
            sb.AppendLine($"        var seek = tx.SeekIndex(\"{prop.IndexName}\", {seekExpr});");
            sb.AppendLine("        while (seek.MoveNext())");
            sb.AppendLine("        {");
            sb.AppendLine("            var id = seek.Current;");
            sb.AppendLine("            results.Add((id, Load(tx, id)));");
            sb.AppendLine("        }");
            sb.AppendLine("        return results;");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitSetProperty(StringBuilder sb, PropertyModel prop)
    {
        if (!_typeMap.TryGetValue(prop.CSharpType, out var map)) return;
        var writeExpr = string.Format(map.write, prop.PropertyName, prop.GraphKey);
        sb.AppendLine($"        tx.SetProperty(id, \"{prop.GraphKey}\", {writeExpr});");
    }

    private static void EmitInsertMultiValue(StringBuilder sb, PropertyModel prop)
    {
        if (!_mvWriteExpr.TryGetValue(prop.CSharpType, out var writeExpr)) return;
        sb.AppendLine($"        if (entity.{prop.PropertyName} != null)");
        sb.AppendLine("        {");
        sb.AppendLine($"            foreach (var __v in entity.{prop.PropertyName})");
        sb.AppendLine($"                tx.AddPropertyValue(id, \"{prop.GraphKey}\", {writeExpr});");
        sb.AppendLine("        }");
    }

    private static void EmitLoadMultiValue(StringBuilder sb, PropertyModel prop)
    {
        if (!_mvReadExpr.TryGetValue(prop.CSharpType, out var readExpr)) return;
        sb.AppendLine("        {");
        sb.AppendLine($"            var __list = new System.Collections.Generic.List<{prop.CSharpType}>();");
        sb.AppendLine($"            var __e = tx.GetPropertyValues(id, \"{prop.GraphKey}\");");
        sb.AppendLine("            while (__e.MoveNext())");
        sb.AppendLine($"                __list.Add({readExpr});");
        sb.AppendLine("            __e.Dispose();");
        sb.AppendLine($"            __entity.{prop.PropertyName} = __list;");
        sb.AppendLine("        }");
    }

    private static void EmitUpdateMultiValue(StringBuilder sb, PropertyModel prop)
    {
        if (!_mvWriteExpr.TryGetValue(prop.CSharpType, out var writeExpr)) return;
        if (!_mvReadExpr.TryGetValue(prop.CSharpType, out var readExpr)) return;
        sb.AppendLine("        {");
        sb.AppendLine($"            var __old = new System.Collections.Generic.HashSet<{prop.CSharpType}>();");
        sb.AppendLine($"            var __e = tx.GetPropertyValues(id, \"{prop.GraphKey}\");");
        sb.AppendLine("            while (__e.MoveNext())");
        sb.AppendLine($"                __old.Add({readExpr});");
        sb.AppendLine("            __e.Dispose();");
        sb.AppendLine($"            if (entity.{prop.PropertyName} != null)");
        sb.AppendLine("            {");
        sb.AppendLine($"                foreach (var __v in entity.{prop.PropertyName})");
        sb.AppendLine("                {");
        sb.AppendLine("                    if (!__old.Remove(__v))");
        sb.AppendLine($"                        tx.AddPropertyValue(id, \"{prop.GraphKey}\", {writeExpr});");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine($"            foreach (var __v in __old)");
        sb.AppendLine($"                tx.RemovePropertyValue(id, \"{prop.GraphKey}\", {writeExpr});");
        sb.AppendLine("        }");
    }
}
