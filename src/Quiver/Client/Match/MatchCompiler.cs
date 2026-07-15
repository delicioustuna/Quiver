using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api.Match;

internal static class MatchCompiler
{
    // (plan, 変数から列へのマップ) を返す。
    // 2 ノード 1 エッジでは Full expand → [source(0), rel(1), neighbor(2)]。
    internal static (IPhysicalOperator plan, Dictionary<string, int> varToColumn) Compile(
        IGraphTransaction tx,
        ISchemaApi schema,
        GraphPattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        var varToColumn = new Dictionary<string, int>();

        LogicalOp builder;
        var startNode = pattern.StartNode;

        builder = new ScanOp(
            EntityKind.Node,
            startNode.Label != null ? schema.GetOrCreateLabel(startNode.Label) : null);

        if (pattern.Edge == null || pattern.EndNode == null)
        {
            // 単一ノードパターン。
            varToColumn[startNode.Variable] = 0;
        }
        else
        {
            // 2 ノード 1 エッジパターン。3 列すべてを残すため Full 出力を使う。
            var edge = pattern.Edge;
            var endNode = pattern.EndNode;
            var direction = edge.Outgoing ? Direction.Outgoing : Direction.Incoming;

            // Full expand の列配置は col0=source, col1=rel, col2=neighbor。
            builder = new ExpandOp(builder, builder.CurrentEntityColumn, direction, edge.Type, ExpandOutputMode.Full, null);
            varToColumn[startNode.Variable] = 0;
            varToColumn[endNode.Variable]   = 2;

            // 終端ノードをラベルで絞り込む。
            if (endNode.Label != null)
            {
                var labelId = schema.GetOrCreateLabel(endNode.Label);
                builder = new FilterOp(builder, _ => new LabelPredicate(labelId, column: 2));
            }
        }

        builder = ApplyWherePredicates(builder, schema, varToColumn, entityKindOf: null, wherePredicates);
        return (PhysicalPlanner.Plan(builder, schema), varToColumn);
    }

    // 星型ハイパーエッジパターンをコンパイルする。最初のメンバーを label scan の anchor とし、
    // node → hyperedge、残りの role member の順に logical op を組む。各メンバー列は
    // 後続の展開を通して carry で持ち越し、同じ Match row に束ねる。
    internal static (IPhysicalOperator plan, Dictionary<string, int> varToColumn) Compile(
        IGraphTransaction tx,
        ISchemaApi schema,
        HyperedgePattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        var members = pattern.Members;
        // 構築時検証。空 role、hyperedge/member 変数の重複はここで拒否する。
        ValidateStarPattern(pattern);

        var varToColumn = new Dictionary<string, int>();
        var entityKindOf = new Dictionary<string, EntityKind>();

        // anchor = 最初のメンバー。label scan を起点にし、node → hyperedge へ展開する。
        var anchor = members[0];
        LogicalOp builder = new ScanOp(
            EntityKind.Node,
            anchor.Node.Label != null ? schema.GetOrCreateLabel(anchor.Node.Label) : null);

        // node → hyperedge。出力は (anchorNode@0, hyperedge@1)。
        // anchor のラベル絞り込みは ScanOp が行うため、追加の filter は不要。
        builder = new ExpandToHyperedgeOp(builder, SourceNodeColumn: 0, pattern.Type, anchor.Role, Carry: null);
        varToColumn[anchor.Node.Variable] = 0;
        varToColumn[pattern.Variable]     = 1;
        entityKindOf[anchor.Node.Variable] = EntityKind.Node;
        entityKindOf[pattern.Variable]     = EntityKind.Hyperedge;

        // 残りのメンバーを hyperedge から順に展開する。ExpandMembersOp は
        // (hyperedge@0, member@1) + carry を放出するので、既存の束縛列を carry で持ち越し、
        // 展開後の新しい列位置へマップし直す。
        for (int i = 1; i < members.Count; i++)
        {
            var member = members[i];
            int hyperedgeColumn = varToColumn[pattern.Variable];

            // 既に束縛済みの列を持ち越す (重複排除 + ソートで決定的にする)。
            int[] carry = new SortedSet<int>(varToColumn.Values).ToArray();

            builder = new ExpandMembersOp(
                builder,
                hyperedgeColumn,
                member.Role,
                ExcludeNodeColumn: null,
                carry);

            // ExpandMembersOp 出力: hyperedge@0, member@1, carry[k] @ (2 + k)。
            var remapped = new Dictionary<string, int>(varToColumn.Count);
            foreach (var (variable, oldColumn) in varToColumn)
            {
                int idx = Array.IndexOf(carry, oldColumn);
                remapped[variable] = 2 + idx;
            }
            varToColumn = remapped;
            varToColumn[member.Node.Variable] = 1;
            entityKindOf[member.Node.Variable] = EntityKind.Node;

            // メンバーのラベル絞り込みは現在の member 列 (1) に適用する。
            if (member.Node.Label != null)
            {
                var labelId = schema.GetOrCreateLabel(member.Node.Label);
                builder = new FilterOp(builder, _ => new LabelPredicate(labelId, column: 1));
            }
        }

        builder = ApplyWherePredicates(builder, schema, varToColumn, entityKindOf, wherePredicates);
        return (PhysicalPlanner.Plan(builder, schema), varToColumn);
    }

    // 星型パターンの構築時検証。空 role、hyperedge/member 変数の重複を拒否する。
    // 未定義 variable は WHERE 適用時 (ApplyWherePredicates) に検出される。
    private static void ValidateStarPattern(HyperedgePattern pattern)
    {
        if (pattern.Members.Count == 0)
            throw new InvalidOperationException("星型ハイパーエッジパターンには少なくとも 1 つの Member が必要です。");

        var seen = new HashSet<string>(StringComparer.Ordinal) { pattern.Variable };
        foreach (var member in pattern.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Role))
                throw new InvalidOperationException("ロール名は空にできません。");
            if (!seen.Add(member.Node.Variable))
            {
                throw new InvalidOperationException(
                    $"パターン変数 '{member.Node.Variable}' が重複しています。"
                    + " ハイパーエッジ変数とメンバー変数はすべて一意である必要があります。");
            }
        }
    }

    // WHERE 述語を対応する列へ適用する。<paramref name="entityKindOf"/> が指定された場合は
    // 変数のエンティティ種別に応じてハイパーエッジ / ノードのプロパティストアを切り替える。
    // null (binary パターン) のときは全変数をノードとして扱う。
    private static LogicalOp ApplyWherePredicates(
        LogicalOp builder,
        ISchemaApi schema,
        Dictionary<string, int> varToColumn,
        Dictionary<string, EntityKind>? entityKindOf,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        foreach (var (variable, key, pred) in wherePredicates)
        {
            if (!varToColumn.TryGetValue(variable, out int entityCol))
                throw new InvalidOperationException($"Unknown variable '{variable}' in WHERE clause.");

            var keyId = schema.GetOrCreatePropertyKey(key);
            var capturedCol  = entityCol;
            var capturedKey  = keyId;
            var capturedPred = pred;
            var entity = entityKindOf != null && entityKindOf.TryGetValue(variable, out var kind)
                             && kind == EntityKind.Hyperedge
                ? PredicateEntity.Hyperedge
                : PredicateEntity.Node;

            if (pred.Kind == PredicateKind.Eq && pred.StringValue != null)
            {
                var strVal = pred.StringValue;
                builder = new FilterOp(builder, _ => new PropertyEqStringPredicate(capturedCol, capturedKey, strVal) { Entity = entity });
            }
            else if (pred.Kind == PredicateKind.Within && pred.WithinValues != null)
            {
                var values = pred.WithinValues;
                builder = new FilterOp(builder, _ => new PropertyWithinStringPredicate(capturedCol, capturedKey, values) { Entity = entity });
            }
            else
            {
                builder = new FilterOp(builder, _ => new PropertyInt64Predicate(capturedCol, capturedKey, capturedPred) { Entity = entity });
            }
        }
        return builder;
    }
}
