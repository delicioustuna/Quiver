using CommunityToolkit.Mvvm.ComponentModel;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class ApiDocumentationViewModel : ObservableObject
{
    private readonly ApiDocumentationService _service;

    [ObservableProperty]
    private IReadOnlyList<ApiTreeNode> _rootNodes = [];

    [ObservableProperty]
    private ApiTreeNode? _selectedNode;

    [ObservableProperty]
    private string _detailTitle = "";

    [ObservableProperty]
    private string _detailKind = "";

    [ObservableProperty]
    private string _detailSignature = "";

    [ObservableProperty]
    private string _detailSummary = "";

    [ObservableProperty]
    private string _detailRemarks = "";

    [ObservableProperty]
    private string _detailReturns = "";

    [ObservableProperty]
    private IReadOnlyList<ApiParamDoc> _detailParameters = [];

    [ObservableProperty]
    private string _filterText = "";

    public ApiDocumentationViewModel(ApiDocumentationService service)
    {
        _service = service;
    }

    public void Refresh()
    {
        RootNodes = BuildTree(_service.Namespaces, FilterText);
    }

    partial void OnFilterTextChanged(string value)
    {
        Refresh();
    }

    partial void OnSelectedNodeChanged(ApiTreeNode? value)
    {
        if (value is null)
        {
            ClearDetail();
            return;
        }

        switch (value.Tag)
        {
            case ApiTypeNode typeNode:
                DetailTitle = typeNode.FullName;
                DetailKind = typeNode.Kind;
                DetailSignature = $"{typeNode.Kind} {typeNode.Name}";
                DetailSummary = typeNode.Documentation.Summary;
                DetailRemarks = typeNode.Documentation.Remarks;
                DetailReturns = "";
                DetailParameters = [];
                break;

            case ApiMemberNode memberNode:
                DetailTitle = memberNode.Name;
                DetailKind = memberNode.Kind.ToString();
                DetailSignature = memberNode.Signature;
                DetailSummary = memberNode.Documentation.Summary;
                DetailRemarks = memberNode.Documentation.Remarks;
                DetailReturns = memberNode.Documentation.Returns;
                DetailParameters = memberNode.Documentation.Parameters;
                break;

            default:
                ClearDetail();
                break;
        }
    }

    public void NavigateToDocId(string docId)
    {
        var member = _service.FindByDocId(docId);
        if (member is not null)
        {
            DetailTitle = member.Name;
            DetailKind = member.Kind.ToString();
            DetailSignature = member.Signature;
            DetailSummary = member.Documentation.Summary;
            DetailRemarks = member.Documentation.Remarks;
            DetailReturns = member.Documentation.Returns;
            DetailParameters = member.Documentation.Parameters;
        }
    }

    private void ClearDetail()
    {
        DetailTitle = "";
        DetailKind = "";
        DetailSignature = "";
        DetailSummary = "";
        DetailRemarks = "";
        DetailReturns = "";
        DetailParameters = [];
    }

    private static IReadOnlyList<ApiTreeNode> BuildTree(
        IReadOnlyList<ApiNamespaceNode> namespaces, string filter)
    {
        var hasFilter = !string.IsNullOrWhiteSpace(filter);
        var result = new List<ApiTreeNode>();

        foreach (var ns in namespaces)
        {
            var typeNodes = new List<ApiTreeNode>();

            foreach (var type in ns.Types)
            {
                var memberNodes = new List<ApiTreeNode>();

                foreach (var member in type.Members)
                {
                    if (hasFilter && !member.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !member.Signature.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    memberNodes.Add(new ApiTreeNode(
                        GetMemberIcon(member.Kind),
                        member.Name,
                        member.Signature,
                        [],
                        member));
                }

                if (hasFilter && memberNodes.Count == 0
                    && !type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var children = hasFilter && memberNodes.Count > 0 ? memberNodes : BuildMemberNodes(type);
                typeNodes.Add(new ApiTreeNode(
                    GetTypeIcon(type.Kind),
                    type.Name,
                    type.Kind,
                    children,
                    type));
            }

            if (typeNodes.Count == 0)
                continue;

            result.Add(new ApiTreeNode("📦", ns.Name, "", typeNodes, null));
        }

        return result;
    }

    private static IReadOnlyList<ApiTreeNode> BuildMemberNodes(ApiTypeNode type)
    {
        var nodes = new List<ApiTreeNode>();
        foreach (var member in type.Members)
        {
            nodes.Add(new ApiTreeNode(
                GetMemberIcon(member.Kind),
                member.Name,
                member.Signature,
                [],
                member));
        }
        return nodes;
    }

    private static string GetTypeIcon(string kind) => kind switch
    {
        "interface" => "🔷",
        "enum" => "🔢",
        "struct" => "🟨",
        "delegate" => "🔗",
        "static class" => "🔧",
        "abstract class" => "🔶",
        _ => "🟦",
    };

    private static string GetMemberIcon(ApiMemberKind kind) => kind switch
    {
        ApiMemberKind.Property => "🔹",
        ApiMemberKind.Method => "▶",
        ApiMemberKind.Event => "⚡",
        ApiMemberKind.EnumValue => "•",
        _ => "·",
    };
}

public sealed record ApiTreeNode(
    string Icon,
    string Title,
    string Detail,
    IReadOnlyList<ApiTreeNode> Children,
    object? Tag);
