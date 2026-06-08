namespace Quiver.Storage.Records;

/// <summary>リレーションシップ走査の方向。</summary>
public enum Direction : byte
{
    /// <summary>出ていくエッジ (起点が自ノード)。</summary>
    Outgoing = 1,
    /// <summary>入ってくるエッジ (終点が自ノード)。</summary>
    Incoming = 2,
    /// <summary>両方向。</summary>
    Both = 3,
}
