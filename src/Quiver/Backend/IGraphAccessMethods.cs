namespace Quiver;

/// <summary>
/// Backend access-method contract. Concrete signatures are introduced in BA-3
/// (codex_advice_3.md §1); BA-1 only exposes the empty marker so backends can
/// already satisfy <see cref="IGraphStorageBackend.Access"/>.
/// </summary>
public interface IGraphAccessMethods
{
}
