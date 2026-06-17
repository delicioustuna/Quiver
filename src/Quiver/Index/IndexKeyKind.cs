namespace Quiver.Index;

/// <summary>
/// B+Tree インデックスのキー型タグ。論理 undo レコード (WAL の
/// IndexMutation) に 1 バイトで載せ、recovery 時にどのコーデックでキーバイト列を
/// デコードするかを決める。<see cref="IndexManager"/> が索引のキーコーデックと
/// 1:1 で対応付ける。
/// </summary>
internal enum IndexKeyKind : byte
{
    Int32 = 1,
    Int64 = 2,
    Double = 3,
    String = 4,
    Bytes = 5,
}
