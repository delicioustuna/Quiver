namespace Quiver.Index;

/// <summary>
/// B+Tree インデックスのキー型タグ。
/// <see cref="IndexManager"/> が索引のキーコーデックと 1:1 で対応付ける。
/// </summary>
internal enum IndexKeyKind : byte
{
    Int32 = 1,
    Int64 = 2,
    Double = 3,
    String = 4,
    Bytes = 5,
}
