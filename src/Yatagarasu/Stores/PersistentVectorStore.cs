using Yatagarasu.Core;

namespace Yatagarasu.Storage.Records;

/// <summary>
/// transactional schema catalog が利用する vector index definition の永続アダプター。
/// vector value と検索構造は保持せず、primary property と immutable segment に委譲する。
/// </summary>
internal sealed class PersistentVectorDefinitionCatalog : IVectorDefinitionCatalog
{
    private readonly SingleFileContainer _container;
    private readonly byte _tenantId;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, VectorIndexDescriptor> _definitions =
        new(StringComparer.Ordinal);
    private VectorDefinitionCatalog? _catalog;

    internal PersistentVectorDefinitionCatalog(
        SingleFileContainer container,
        byte tenantId)
    {
        _container = container;
        _tenantId = tenantId;
        if (!_container.HasTenant(tenantId))
            return;

        _catalog = new VectorDefinitionCatalog(
            _container.OpenTenant(tenantId, PageKind.Header));
        Reload();
    }

    private VectorDefinitionCatalog Catalog => _catalog ??=
        new VectorDefinitionCatalog(_container.OpenTenant(_tenantId, PageKind.Header));

    public void Create(VectorIndexDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        VectorIndexDescriptorValidator.Validate(descriptor);
        lock (_gate)
        {
            if (_definitions.ContainsKey(descriptor.Name))
            {
                throw new VectorException(
                    $"Vector index '{descriptor.Name}' already exists.");
            }
            Catalog.Register(descriptor);
            _definitions.Add(descriptor.Name, descriptor);
        }
    }

    public void Drop(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_definitions.Remove(name))
                throw new VectorException($"Vector index '{name}' does not exist.");
            Catalog.Unregister(name);
        }
    }

    public bool TryGet(string name, out VectorIndexDescriptor descriptor)
    {
        descriptor = default!;
        if (string.IsNullOrEmpty(name))
            return false;
        lock (_gate)
            return _definitions.TryGetValue(name, out descriptor!);
    }

    public IReadOnlyList<VectorIndexDescriptor> List()
    {
        lock (_gate)
            return _definitions.Values.ToArray();
    }

    public void Reload()
    {
        lock (_gate)
        {
            if (_catalog is null)
                return;
            _catalog.Reload();
            _definitions.Clear();
            foreach (VectorIndexDescriptor descriptor in _catalog.Entries)
                _definitions.Add(descriptor.Name, descriptor);
        }
    }
}
