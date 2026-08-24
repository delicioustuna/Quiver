using System.Text;

namespace Yatagarasu.SourceGen;

internal static class GraphEdgeEmitter
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
    };

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

    private static readonly Dictionary<string, (string write, string read)> _publicTypeMap = new()
    {
        ["string"]  = ("entity.{0}", "read.Get(edge, \"{1}\").AsString()"),
        ["string?"] = ("entity.{0} ?? \"\"", "read.Get(edge, \"{1}\").AsString()"),
        ["int"]     = ("entity.{0}", "read.Get(edge, \"{1}\").AsInt32()"),
        ["long"]    = ("entity.{0}", "read.Get(edge, \"{1}\").AsInt64()"),
        ["double"]  = ("entity.{0}", "read.Get(edge, \"{1}\").AsDouble()"),
        ["float"]   = ("Yatagarasu.GraphValue.FromDouble((double)entity.{0})", "(float)read.Get(edge, \"{1}\").AsDouble()"),
        ["Half"]    = ("Yatagarasu.GraphValue.FromDouble((double)entity.{0})", "(System.Half)read.Get(edge, \"{1}\").AsDouble()"),
        ["bool"]    = ("entity.{0}", "read.Get(edge, \"{1}\").AsBoolean()"),
        ["DateTime"]       = ("Yatagarasu.GraphValue.FromDateTime(entity.{0})", "read.Get(edge, \"{1}\").AsDateTime()"),
        ["DateTimeOffset"] = ("Yatagarasu.GraphValue.FromDateTimeOffset(entity.{0})", "read.Get(edge, \"{1}\").AsDateTimeOffset()"),
        ["DateOnly"]       = ("Yatagarasu.GraphValue.FromDateOnly(entity.{0})", "read.Get(edge, \"{1}\").AsDateOnly()"),
        ["TimeOnly"]       = ("Yatagarasu.GraphValue.FromTimeOnly(entity.{0})", "read.Get(edge, \"{1}\").AsTimeOnly()"),
        ["TimeSpan"]       = ("Yatagarasu.GraphValue.FromTimeSpan(entity.{0})", "read.Get(edge, \"{1}\").AsTimeSpan()"),
    };

    private static readonly Dictionary<string, (string write, string read)> _publicMultiValueMap = new()
    {
        ["string"]  = ("__v", "__value.AsString()"),
        ["int"]     = ("__v", "__value.AsInt32()"),
        ["long"]    = ("__v", "__value.AsInt64()"),
        ["double"]  = ("__v", "__value.AsDouble()"),
        ["float"]   = ("Yatagarasu.GraphValue.FromDouble((double)__v)", "(float)__value.AsDouble()"),
        ["Half"]    = ("Yatagarasu.GraphValue.FromDouble((double)__v)", "(System.Half)__value.AsDouble()"),
        ["bool"]    = ("__v", "__value.AsBoolean()"),
        ["DateTime"]       = ("Yatagarasu.GraphValue.FromDateTime(__v)", "__value.AsDateTime()"),
        ["DateTimeOffset"] = ("Yatagarasu.GraphValue.FromDateTimeOffset(__v)", "__value.AsDateTimeOffset()"),
        ["DateOnly"]       = ("Yatagarasu.GraphValue.FromDateOnly(__v)", "__value.AsDateOnly()"),
        ["TimeOnly"]       = ("Yatagarasu.GraphValue.FromTimeOnly(__v)", "__value.AsTimeOnly()"),
        ["TimeSpan"]       = ("Yatagarasu.GraphValue.FromTimeSpan(__v)", "__value.AsTimeSpan()"),
    };

    public static string Emit(GraphEdgeModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Yatagarasu;");
        sb.AppendLine("using Yatagarasu.Api;");
        sb.AppendLine("using Yatagarasu.Core;");
        sb.AppendLine("using Yatagarasu.Storage.Records;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.AppendLine($"namespace {model.Namespace};");
            sb.AppendLine();
        }

        var multiValueProps = model.Properties.FindAll(p => p.IsMultiValued);

        sb.AppendLine("#if YATAGARASU_LEGACY_GENERATED_API");
        sb.AppendLine($"partial class {model.ClassName} : Yatagarasu.Api.IGraphEdge<{model.ClassName}, {model.SourceFqn}, {model.TargetFqn}>, Yatagarasu.IGraphEdgeEntity<{model.ClassName}>");
        sb.AppendLine("#else");
        sb.AppendLine($"partial class {model.ClassName} : Yatagarasu.IGraphEdgeEntity<{model.ClassName}>");
        sb.AppendLine("#endif");
        sb.AppendLine("{");
        sb.AppendLine($"    public static string GraphType => \"{model.EdgeType}\";");
        sb.AppendLine();
        sb.AppendLine("#if YATAGARASU_LEGACY_GENERATED_API");

        // 挿入
        sb.AppendLine($"    public static Yatagarasu.Core.EdgeId Insert(IWriteTransaction tx, Yatagarasu.Core.VertexId from, Yatagarasu.Core.VertexId to, {model.ClassName} entity)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var id = tx.CreateEdge(from, to, \"{model.EdgeType}\");");
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

        // 読み込み
        sb.AppendLine($"    public static {model.ClassName} Load(IReadTransaction tx, Yatagarasu.Core.EdgeId id)");
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
        sb.AppendLine($"    public static void Update(IWriteTransaction tx, Yatagarasu.Core.EdgeId id, {model.ClassName} entity)");
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

        // 構造置換: endpoint/type は新Edgeへ反映し、コピー済みpropertyをtarget modelで上書きする。
        sb.AppendLine($"    public static Yatagarasu.EdgeReplacement Replace(IWriteTransaction tx, Yatagarasu.Core.EdgeId id, Yatagarasu.Core.VertexId from, Yatagarasu.Core.VertexId to, {model.ClassName} entity)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var __replacement = tx.ReplaceEdge(id, from, to, \"{model.EdgeType}\");");
        sb.AppendLine("        Update(tx, __replacement.NewId, entity);");
        sb.AppendLine("        return __replacement;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // 削除
        sb.AppendLine($"    public static void Delete(IWriteTransaction tx, Yatagarasu.Core.EdgeId id) => tx.DeleteEdge(id);");

        sb.AppendLine("#endif");
        EmitPublicMapper(sb, model, multiValueProps);

        sb.AppendLine("}");
        sb.AppendLine();

        // ホップ後の型を保存するトラバーサル糖衣
        sb.AppendLine("#if YATAGARASU_LEGACY_GENERATED_API");
        sb.AppendLine($"/// <summary>{model.ClassName} (型保存トラバーサル糖衣) — SourceGenerator 生成。</summary>");
        var traversalAccess = model.IsPublic ? "public" : "internal";
        sb.AppendLine($"{traversalAccess} static class {model.ClassName}TraversalExtensions");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>{model.SourceFqn} から {model.EdgeType} を外向に辿り、{model.TargetFqn} 型を保存する。</summary>");
        sb.AppendLine($"    public static Yatagarasu.Api.TypedGraphTraversal<{model.TargetFqn}> {model.ClassName}(this Yatagarasu.Api.TypedGraphTraversal<{model.SourceFqn}> source)");
        sb.AppendLine($"        => source.Out<{model.ClassName}, {model.TargetFqn}>();");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>{model.EdgeType} エッジを式ツリー述語で絞り込んでから {model.TargetFqn} 型を保存して辿る。</summary>");
        sb.AppendLine($"    public static Yatagarasu.Api.TypedGraphTraversal<{model.TargetFqn}> {model.ClassName}(this Yatagarasu.Api.TypedGraphTraversal<{model.SourceFqn}> source, System.Linq.Expressions.Expression<System.Func<{model.ClassName}, bool>> edgeFilter)");
        sb.AppendLine($"        => source.OutWhere<{model.ClassName}, {model.TargetFqn}>(edgeFilter);");
        sb.AppendLine();

        // 書き込み終端の糖衣
        var s = model.SourceFqn;
        var t = model.TargetFqn;
        var r = model.ClassName;

        sb.AppendLine($"    /// <summary>{s} と {t} の直積に {model.EdgeType} 辺を生成する。</summary>");
        sb.AppendLine($"    public static long Add{r}(this Yatagarasu.Api.GraphMutationSource mutation, Yatagarasu.Api.TypedGraphTraversal<{s}> sources, Yatagarasu.Api.TypedGraphTraversal<{t}> targets)");
        sb.AppendLine($"        => Yatagarasu.Api.TypedGraphTraversalWriteExtensions.AddEdge<{s}, {r}, {t}>(mutation, sources, targets);");
        sb.AppendLine();

        sb.AppendLine($"    /// <summary>始点ごとに終点を求め {model.EdgeType} 辺を生成する (相関版)。</summary>");
        sb.AppendLine($"    public static long Add{r}(this Yatagarasu.Api.GraphMutationSource mutation, Yatagarasu.Api.TypedGraphTraversal<{s}> sources, System.Func<{s}, Yatagarasu.Api.TypedGraphTraversal<{t}>> targets)");
        sb.AppendLine($"        => Yatagarasu.Api.TypedGraphTraversalWriteExtensions.AddEdge<{s}, {r}, {t}>(mutation, sources, targets);");
        sb.AppendLine();

        sb.AppendLine($"    /// <summary>{s} と {t} の直積で {model.EdgeType} 辺を upsert する。</summary>");
        sb.AppendLine($"    public static (long Created, long Matched) Merge{r}(this Yatagarasu.Api.GraphMutationSource mutation, Yatagarasu.Api.TypedGraphTraversal<{s}> sources, Yatagarasu.Api.TypedGraphTraversal<{t}> targets)");
        sb.AppendLine($"        => Yatagarasu.Api.TypedGraphTraversalWriteExtensions.MergeEdge<{s}, {r}, {t}>(mutation, sources, targets);");
        sb.AppendLine();

        sb.AppendLine($"    /// <summary>始点ごとに終点を求め {model.EdgeType} 辺を upsert する (相関版)。</summary>");
        sb.AppendLine($"    public static (long Created, long Matched) Merge{r}(this Yatagarasu.Api.GraphMutationSource mutation, Yatagarasu.Api.TypedGraphTraversal<{s}> sources, System.Func<{s}, Yatagarasu.Api.TypedGraphTraversal<{t}>> targets)");
        sb.AppendLine($"        => Yatagarasu.Api.TypedGraphTraversalWriteExtensions.MergeEdge<{s}, {r}, {t}>(mutation, sources, targets);");

        sb.AppendLine("}");
        sb.AppendLine("#endif");
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

    private static void EmitPublicMapper(
        StringBuilder sb,
        GraphEdgeModel model,
        List<PropertyModel> multiValueProps)
    {
        sb.AppendLine();
        sb.AppendLine($"    public static {model.ClassName} Read(Yatagarasu.GraphReadAccess read, Yatagarasu.EdgeKey edge)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var __entity = new {model.ClassName}");
        sb.AppendLine("        {");
        foreach (var prop in model.Properties)
        {
            if (!prop.IsMultiValued && _publicTypeMap.TryGetValue(prop.CSharpType, out var map))
                sb.AppendLine($"            {prop.PropertyName} = {string.Format(map.read, prop.PropertyName, prop.GraphKey)},");
        }
        sb.AppendLine("        };");
        foreach (var prop in multiValueProps)
        {
            if (!_publicMultiValueMap.TryGetValue(prop.CSharpType, out var map)) continue;
            sb.AppendLine("        {");
            sb.AppendLine($"            var __list = new System.Collections.Generic.List<{prop.CSharpType}>();");
            sb.AppendLine($"            foreach (var __value in read.GetValues(edge, \"{prop.GraphKey}\"))");
            sb.AppendLine($"                __list.Add({map.read});");
            sb.AppendLine($"            __entity.{prop.PropertyName} = __list;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return __entity;");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine($"    public static void Write(Yatagarasu.GraphWriteAccess write, Yatagarasu.EdgeKey edge, {model.ClassName} entity)");
        sb.AppendLine("    {");
        foreach (var prop in model.Properties)
        {
            if (prop.IsMultiValued)
            {
                EmitPublicMultiValueWrite(sb, prop);
            }
            else if (_publicTypeMap.TryGetValue(prop.CSharpType, out var map))
            {
                sb.AppendLine($"        write.Set(edge, \"{prop.GraphKey}\", {string.Format(map.write, prop.PropertyName, prop.GraphKey)});");
            }
        }
        sb.AppendLine("    }");
    }

    private static void EmitPublicMultiValueWrite(StringBuilder sb, PropertyModel prop)
    {
        if (!_publicMultiValueMap.TryGetValue(prop.CSharpType, out var map)) return;
        sb.AppendLine("        {");
        sb.AppendLine($"            var __old = new System.Collections.Generic.HashSet<{prop.CSharpType}>();");
        sb.AppendLine($"            foreach (var __value in write.GetValues(edge, \"{prop.GraphKey}\"))");
        sb.AppendLine($"                __old.Add({map.read});");
        sb.AppendLine($"            if (entity.{prop.PropertyName} != null)");
        sb.AppendLine("            {");
        sb.AppendLine($"                foreach (var __v in entity.{prop.PropertyName})");
        sb.AppendLine("                {");
        sb.AppendLine("                    if (!__old.Remove(__v))");
        sb.AppendLine($"                        write.AddValue(edge, \"{prop.GraphKey}\", {map.write});");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            foreach (var __v in __old)");
        sb.AppendLine($"                write.RemoveValue(edge, \"{prop.GraphKey}\", {map.write});");
        sb.AppendLine("        }");
    }
}
