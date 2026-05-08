namespace GraphDb.Engine.Codec.Schema;

/// <summary>
/// レコードフィールドのバイトオフセットを指定する属性。
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class FieldAttribute : Attribute
{
    public int Offset { get; }
    public FieldAttribute(int offset) { Offset = offset; }
}
