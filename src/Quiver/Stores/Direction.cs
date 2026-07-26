namespace Quiver.Storage.Records;

/// <summary>Edge走査の方向。</summary>
public enum Direction : byte
{
    /// <summary>出ていくエッジ (起点が自Vertex)。</summary>
    Outgoing = 1,
    /// <summary>入ってくるエッジ (終点が自Vertex)。</summary>
    Incoming = 2,
    /// <summary>両方向。</summary>
    Both = 3,
}
