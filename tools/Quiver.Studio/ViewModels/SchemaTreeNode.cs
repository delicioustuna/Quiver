using System.Collections.ObjectModel;

namespace Quiver.Studio.ViewModels;

public sealed class SchemaTreeNode
{
    public string Title { get; }
    public string? Detail { get; }
    public string Icon { get; }
    public ObservableCollection<SchemaTreeNode> Children { get; }
    public bool IsFolder => Children.Count > 0 || _isFolder;

    private readonly bool _isFolder;

    public SchemaTreeNode(string title, string icon, bool isFolder = false, string? detail = null)
    {
        Title = title;
        Icon = icon;
        Detail = detail;
        Children = [];
        _isFolder = isFolder;
    }

    public static SchemaTreeNode Folder(string title, string icon, IEnumerable<SchemaTreeNode> children)
    {
        var node = new SchemaTreeNode(title, icon, isFolder: true);
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }

    public static SchemaTreeNode Leaf(string title, string icon, string? detail = null)
        => new(title, icon, detail: detail);
}
