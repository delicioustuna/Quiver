namespace GraphDb.Engine.Codec.Schema;

/// <summary>
/// 固定長レコードを宣言的に定義する属性。
/// Source Generator がオフセット定数とアクセサを自動生成する。
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class FixedRecordAttribute : Attribute
{
    public int Size { get; }
    public FixedRecordAttribute(int size) { Size = size; }
}
