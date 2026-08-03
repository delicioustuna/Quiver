using Quiver.Core;

namespace Quiver;

/// <summary>公開 API で使用する不透明な Vertex 識別子。</summary>
public readonly struct VertexKey : IEquatable<VertexKey>
{
    private readonly long _value;

    internal VertexKey(VertexId value) => _value = value.Value;

    /// <summary>有効な識別子かどうかを返す。</summary>
    public bool IsValid => _value >= 0;

    internal VertexId ToCore() => new(_value);

    /// <inheritdoc />
    public bool Equals(VertexKey other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VertexKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => IsValid ? $"vertex:{_value:X}" : "vertex:invalid";

    /// <summary>二つの識別子が等しいかを返す。</summary>
    public static bool operator ==(VertexKey left, VertexKey right) => left.Equals(right);

    /// <summary>二つの識別子が異なるかを返す。</summary>
    public static bool operator !=(VertexKey left, VertexKey right) => !left.Equals(right);
}

/// <summary>公開 API で使用する不透明な Edge 識別子。</summary>
public readonly struct EdgeKey : IEquatable<EdgeKey>
{
    private readonly long _value;

    internal EdgeKey(EdgeId value) => _value = value.Value;

    /// <summary>有効な識別子かどうかを返す。</summary>
    public bool IsValid => _value >= 0;

    internal EdgeId ToCore() => new(_value);

    /// <inheritdoc />
    public bool Equals(EdgeKey other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => IsValid ? $"edge:{_value:X}" : "edge:invalid";

    /// <summary>二つの識別子が等しいかを返す。</summary>
    public static bool operator ==(EdgeKey left, EdgeKey right) => left.Equals(right);

    /// <summary>二つの識別子が異なるかを返す。</summary>
    public static bool operator !=(EdgeKey left, EdgeKey right) => !left.Equals(right);
}

/// <summary>公開 API で使用する不透明な Nexus 識別子。</summary>
public readonly struct NexusKey : IEquatable<NexusKey>
{
    private readonly long _value;

    internal NexusKey(NexusId value) => _value = value.Value;

    /// <summary>有効な識別子かどうかを返す。</summary>
    public bool IsValid => _value >= 0;

    internal NexusId ToCore() => new(_value);

    /// <inheritdoc />
    public bool Equals(NexusKey other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NexusKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => IsValid ? $"nexus:{_value:X}" : "nexus:invalid";

    /// <summary>二つの識別子が等しいかを返す。</summary>
    public static bool operator ==(NexusKey left, NexusKey right) => left.Equals(right);

    /// <summary>二つの識別子が異なるかを返す。</summary>
    public static bool operator !=(NexusKey left, NexusKey right) => !left.Equals(right);
}

/// <summary>Edge の走査方向。</summary>
public enum GraphDirection
{
    /// <summary>始点から終点へ向かう Edge。</summary>
    Outgoing,

    /// <summary>終点から始点へ向かう Edge。</summary>
    Incoming,

    /// <summary>両方向の Edge。</summary>
    Both,
}

/// <summary>現在の snapshot で読み取った Edge。</summary>
/// <param name="Key">Edge 識別子。</param>
/// <param name="Source">始点 Vertex。</param>
/// <param name="Target">終点 Vertex。</param>
/// <param name="Type">Edge 型名。</param>
public readonly record struct GraphEdge(EdgeKey Key, VertexKey Source, VertexKey Target, string Type);

/// <summary>Nexus に参加する Vertex とそのロール。</summary>
/// <param name="Role">ロール名。</param>
/// <param name="Vertex">参加 Vertex。</param>
public readonly record struct GraphNexusMember(string Role, VertexKey Vertex);

internal static class GraphIdentityConversions
{
    internal static Storage.Records.Direction ToCore(this GraphDirection direction) => direction switch
    {
        GraphDirection.Outgoing => Storage.Records.Direction.Outgoing,
        GraphDirection.Incoming => Storage.Records.Direction.Incoming,
        GraphDirection.Both => Storage.Records.Direction.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };
}
