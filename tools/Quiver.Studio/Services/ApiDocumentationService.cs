using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ReflectionPropertyInfo = System.Reflection.PropertyInfo;

namespace Quiver.Studio.Services;

public sealed class ApiDocumentationService
{
    private readonly ILogger<ApiDocumentationService> _logger;
    private IReadOnlyList<ApiNamespaceNode>? _namespaces;
    private Dictionary<string, ApiMemberNode>? _docIdIndex;

    public ApiDocumentationService(ILogger<ApiDocumentationService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ApiNamespaceNode> Namespaces => _namespaces ?? [];

    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var xmlDocs = LoadXmlDocs();
                _namespaces = BuildTree(typeof(QuiverDatabase).Assembly, xmlDocs);
                _docIdIndex = BuildDocIdIndex(_namespaces);
                _logger.LogInformation("API ドキュメント初期化完了: {Count} 名前空間", _namespaces.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "API ドキュメント初期化失敗");
            }
        });
    }

    public ApiMemberNode? FindByDocId(string docId)
    {
        if (_docIdIndex is not null && _docIdIndex.TryGetValue(docId, out var node))
            return node;
        return null;
    }

    public ApiTypeNode? FindType(string fullTypeName)
    {
        if (_namespaces is null) return null;
        foreach (var ns in _namespaces)
            foreach (var type in ns.Types)
                if (type.FullName == fullTypeName)
                    return type;
        return null;
    }

    private static Dictionary<string, XElement> LoadXmlDocs()
    {
        var dllPath = typeof(QuiverDatabase).Assembly.Location;
        if (string.IsNullOrEmpty(dllPath))
            return new();

        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath))
            return new();

        try
        {
            var dict = new Dictionary<string, XElement>();
            var doc = XDocument.Load(xmlPath);
            foreach (var member in doc.Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (name is not null)
                    dict[name] = member;
            }
            return dict;
        }
        catch
        {
            return new();
        }
    }

    private static IReadOnlyList<ApiNamespaceNode> BuildTree(
        Assembly assembly, Dictionary<string, XElement> xmlDocs)
    {
        var publicTypes = assembly.GetExportedTypes()
            .Where(t => !t.IsNested)
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name);

        var groups = publicTypes.GroupBy(t => t.Namespace ?? "(global)");
        var result = new List<ApiNamespaceNode>();

        foreach (var g in groups)
        {
            var types = new List<ApiTypeNode>();
            foreach (var type in g)
                types.Add(BuildTypeNode(type, xmlDocs));
            result.Add(new ApiNamespaceNode(g.Key, types));
        }

        return result;
    }

    private static ApiTypeNode BuildTypeNode(Type type, Dictionary<string, XElement> xmlDocs)
    {
        var docId = $"T:{type.FullName}";
        var doc = xmlDocs.GetValueOrDefault(docId);

        var members = new List<ApiMemberNode>();

        var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var prop in type.GetProperties(bindingFlags).OrderBy(p => p.Name))
        {
            var memberDocId = $"P:{type.FullName}.{prop.Name}";
            var memberDoc = xmlDocs.GetValueOrDefault(memberDocId);
            members.Add(new ApiMemberNode(
                prop.Name,
                ApiMemberKind.Property,
                FormatReflectionPropertySignature(prop),
                memberDocId,
                ExtractDoc(memberDoc)));
        }

        foreach (var method in type.GetMethods(bindingFlags)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name))
        {
            var memberDocId = BuildMethodDocId(method);
            var memberDoc = xmlDocs.GetValueOrDefault(memberDocId);
            members.Add(new ApiMemberNode(
                method.Name,
                ApiMemberKind.Method,
                FormatMethodSignature(method),
                memberDocId,
                ExtractDoc(memberDoc)));
        }

        foreach (var evt in type.GetEvents(bindingFlags).OrderBy(e => e.Name))
        {
            var memberDocId = $"E:{type.FullName}.{evt.Name}";
            var memberDoc = xmlDocs.GetValueOrDefault(memberDocId);
            members.Add(new ApiMemberNode(
                evt.Name,
                ApiMemberKind.Event,
                $"event {FormatTypeName(evt.EventHandlerType!)} {evt.Name}",
                memberDocId,
                ExtractDoc(memberDoc)));
        }

        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .OrderBy(f => f.Name))
            {
                var memberDocId = $"F:{type.FullName}.{field.Name}";
                var memberDoc = xmlDocs.GetValueOrDefault(memberDocId);
                members.Add(new ApiMemberNode(
                    field.Name,
                    ApiMemberKind.EnumValue,
                    $"{field.Name} = {field.GetRawConstantValue()}",
                    memberDocId,
                    ExtractDoc(memberDoc)));
            }
        }

        return new ApiTypeNode(
            type.Name,
            type.FullName ?? type.Name,
            GetTypeKindLabel(type),
            docId,
            ExtractDoc(doc),
            members);
    }

    private static string BuildMethodDocId(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var typeName = method.DeclaringType?.FullName ?? "";
        if (parameters.Length == 0)
            return $"M:{typeName}.{method.Name}";

        var paramTypes = string.Join(",", parameters.Select(p => FormatDocIdType(p.ParameterType)));
        return $"M:{typeName}.{method.Name}({paramTypes})";
    }

    private static string FormatDocIdType(Type type)
    {
        if (type.IsByRef)
            return FormatDocIdType(type.GetElementType()!) + "@";
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition().FullName!;
            var tick = def.IndexOf('`');
            var baseName = tick >= 0 ? def[..tick] : def;
            var args = string.Join(",", type.GetGenericArguments().Select(FormatDocIdType));
            return $"{baseName}{{{args}}}";
        }
        return type.FullName ?? type.Name;
    }

    private static string FormatTypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(object)) return "object";

        if (type.IsByRef)
            return "ref " + FormatTypeName(type.GetElementType()!);

        if (type.IsGenericType)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{name}<{args}>";
        }

        return type.Name;
    }

    private static string FormatReflectionPropertySignature(ReflectionPropertyInfo prop)
    {
        var accessors = new List<string>();
        if (prop.GetMethod is not null) accessors.Add("get");
        if (prop.SetMethod is not null) accessors.Add("set");
        return $"{FormatTypeName(prop.PropertyType)} {prop.Name} {{ {string.Join("; ", accessors)}; }}";
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var paramStr = string.Join(", ", parameters.Select(p =>
        {
            var prefix = p.ParameterType.IsByRef
                ? (p.IsIn ? "in " : p.IsOut ? "out " : "ref ")
                : "";
            return $"{prefix}{FormatTypeName(p.ParameterType)} {p.Name}";
        }));

        var returnType = FormatTypeName(method.ReturnType);
        if (method.IsStatic)
            return $"static {returnType} {method.Name}({paramStr})";
        return $"{returnType} {method.Name}({paramStr})";
    }

    private static string GetTypeKindLabel(Type type) => type switch
    {
        _ when type.IsEnum => "enum",
        _ when type.IsValueType => "struct",
        _ when type.IsInterface => "interface",
        _ when type.IsAbstract && type.IsSealed => "static class",
        _ when type.IsAbstract => "abstract class",
        _ when type.BaseType == typeof(MulticastDelegate) => "delegate",
        _ => "class",
    };

    private static ApiDocumentation ExtractDoc(XElement? element)
    {
        if (element is null)
            return ApiDocumentation.Empty;

        var summary = CleanXmlText(element.Element("summary"));
        var remarks = CleanXmlText(element.Element("remarks"));
        var returns = CleanXmlText(element.Element("returns"));

        var parameters = new List<ApiParamDoc>();
        foreach (var param in element.Elements("param"))
        {
            var name = param.Attribute("name")?.Value ?? "";
            var desc = CleanXmlText(param);
            parameters.Add(new ApiParamDoc(name, desc));
        }

        return new ApiDocumentation(summary, remarks, returns, parameters);
    }

    private static string CleanXmlText(XElement? element)
    {
        if (element is null) return "";
        var text = element.Value;
        return string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }

    private static Dictionary<string, ApiMemberNode> BuildDocIdIndex(IReadOnlyList<ApiNamespaceNode> namespaces)
    {
        var dict = new Dictionary<string, ApiMemberNode>();
        foreach (var ns in namespaces)
            foreach (var type in ns.Types)
                foreach (var member in type.Members)
                    dict.TryAdd(member.DocId, member);
        return dict;
    }
}

public sealed record ApiNamespaceNode(string Name, IReadOnlyList<ApiTypeNode> Types);

public sealed record ApiTypeNode(
    string Name,
    string FullName,
    string Kind,
    string DocId,
    ApiDocumentation Documentation,
    IReadOnlyList<ApiMemberNode> Members);

public sealed record ApiMemberNode(
    string Name,
    ApiMemberKind Kind,
    string Signature,
    string DocId,
    ApiDocumentation Documentation);

public enum ApiMemberKind { Property, Method, Event, EnumValue }

public sealed record ApiDocumentation(
    string Summary,
    string Remarks,
    string Returns,
    IReadOnlyList<ApiParamDoc> Parameters)
{
    public static ApiDocumentation Empty { get; } = new("", "", "", []);
    public bool HasContent => Summary.Length > 0 || Remarks.Length > 0;
}

public sealed record ApiParamDoc(string Name, string Description);
