using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// <see cref="GraphMutationSource.AddNexus"/>から開始し、ロール付きメンバーと
/// プロパティを蓄積してNexusを作成するビルダ。
/// </summary>
/// <remarks>
/// Nexusは二つの端点に限られない関係を表す。たとえば購入を buyer、item、
/// seller の各ロールで表したり、抽出ファクトへ subject、object、source をまとめて
/// 結び付けたりできる。メンバー集合は作成後に変更できない。
/// <para>
/// <see cref="Next"/> が成功すると蓄積済みのメンバーとプロパティは消去されるため、
/// 同じ型の別Nexusを続けて作成する用途にビルダを再利用できる。
/// </para>
/// </remarks>
internal sealed class NexusBuilder
{
    private readonly IWriteTransaction _tx;
    private readonly string _type;
    private readonly List<NexusMember> _members = new();
    private readonly List<Action<IWriteTransaction, NexusId>> _properties = new();

    internal NexusBuilder(IWriteTransaction tx, string type)
    {
        _tx = tx;
        _type = type;
    }

    /// <summary>指定ロールで参加するVertexを追加する。</summary>
    /// <param name="role">関係内でVertexが担うロール名。</param>
    /// <param name="vertexId">参加するVertex ID。</param>
    /// <returns>このビルダ。</returns>
    /// <remarks>
    /// 同じロールへ複数Vertexを追加でき、同じVertexを異なるロールで参加させることもできる。
    /// 同一のロールとVertexの組を重複して追加した場合は <see cref="Next"/> が拒否する。
    /// </remarks>
    public NexusBuilder Member(string role, VertexId vertexId)
    {
        _members.Add(new NexusMember(role, vertexId));
        return this;
    }

    /// <summary>文字列プロパティを追加する。</summary>
    public NexusBuilder P(string key, string value) =>
        AddProperty((tx, id) => tx.SetProperty(id, key, PropertyValue.FromString(value)));

    /// <summary><see cref="int"/> プロパティを追加する。</summary>
    public NexusBuilder P(string key, int value) =>
        AddProperty((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt32(value)));

    /// <summary><see cref="long"/> プロパティを追加する。</summary>
    public NexusBuilder P(string key, long value) =>
        AddProperty((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt64(value)));

    /// <summary><see cref="double"/> プロパティを追加する。</summary>
    public NexusBuilder P(string key, double value) =>
        AddProperty((tx, id) => tx.SetProperty(id, key, PropertyValue.FromDouble(value)));

    /// <summary><see cref="bool"/> プロパティを追加する。</summary>
    public NexusBuilder P(string key, bool value) =>
        AddProperty((tx, id) => tx.SetProperty(id, key, PropertyValue.FromBool(value)));

    /// <summary>蓄積したメンバーをスナップショットしてNexusを作成する。</summary>
    /// <returns>作成されたNexus ID。</returns>
    /// <exception cref="ArgumentException">
    /// 型名またはロール名が空、メンバーが 2 件未満、同一メンバーが重複、
    /// あるいは参照Vertexが存在しない場合。
    /// </exception>
    /// <remarks>
    /// 作成コストはメンバー数を A として O(A)。成功時にだけビルダの蓄積状態を消去する。
    /// </remarks>
    public NexusId Next()
    {
        // CreateNexus 中のコールバックや後続のビルダ再利用から List の可変状態を
        // 観測させない。配列化した作成単位をトランザクションへ渡す。
        NexusMember[] members = _members.ToArray();
        NexusId id = _tx.CreateNexus(_type, members);
        Action<IWriteTransaction, NexusId>[] properties = _properties.ToArray();
        foreach (var apply in properties)
            apply(_tx, id);

        _members.Clear();
        _properties.Clear();
        return id;
    }

    private NexusBuilder AddProperty(Action<IWriteTransaction, NexusId> property)
    {
        _properties.Add(property);
        return this;
    }
}
