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
    // 2 Vertex 1 エッジでは Full expand → [source(0), edge(1), neighbor(2)]。
    internal static (IPhysicalOperator plan, Dictionary<string, int> varToColumn) Compile(
        IGraphTransaction tx,
        ISchemaApi schema,
        GraphPattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        var varToColumn = new Dictionary<string, int>();

        LogicalOp builder;
        var startVertex = pattern.StartVertex;

        builder = new ScanOp(
            EntityKind.Vertex,
            startVertex.Label != null ? schema.GetOrCreateLabel(startVertex.Label) : null);

        if (pattern.Edge == null || pattern.EndVertex == null)
        {
            // 単一Vertexパターン。
            varToColumn[startVertex.Variable] = 0;
        }
        else
        {
            // 2 Vertex 1 エッジパターン。3 列すべてを残すため Full 出力を使う。
            var edge = pattern.Edge;
            var endVertex = pattern.EndVertex;
            var direction = edge.Outgoing ? Direction.Outgoing : Direction.Incoming;

            // Full expand の列配置は col0=source, col1=edge, col2=neighbor。
            builder = new ExpandOp(builder, builder.CurrentEntityColumn, direction, edge.Type, ExpandOutputMode.Full, null);
            varToColumn[startVertex.Variable] = 0;
            varToColumn[endVertex.Variable]   = 2;

            // 終端Vertexをラベルで絞り込む。
            if (endVertex.Label != null)
            {
                var labelId = schema.GetOrCreateLabel(endVertex.Label);
                builder = new FilterOp(builder, _ => new LabelPredicate(labelId, column: 2));
            }
        }

        builder = ApplyWherePredicates(builder, schema, varToColumn, entityKindOf: null, wherePredicates);
        return (PhysicalPlanner.Plan(builder, schema), varToColumn);
    }

    // 星型Nexusパターンをコンパイルする。最初のメンバーを label scan の anchor とし、
    // vertex → nexus、残りの role member の順に logical op を組む。各メンバー列は
    // 後続の展開を通して carry で持ち越し、同じ Match row に束ねる。
    internal static (IPhysicalOperator plan, Dictionary<string, int> varToColumn) Compile(
        IGraphTransaction tx,
        ISchemaApi schema,
        NexusPattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        var members = pattern.Members;
        // 構築時検証。空 role、nexus/member 変数の重複はここで拒否する。
        ValidateStarPattern(pattern);

        var varToColumn = new Dictionary<string, int>();
        var entityKindOf = new Dictionary<string, EntityKind>();

        // anchor = 最初のメンバー。label scan を起点にし、vertex → nexus へ展開する。
        var anchor = members[0];
        LogicalOp builder = new ScanOp(
            EntityKind.Vertex,
            anchor.Vertex.Label != null ? schema.GetOrCreateLabel(anchor.Vertex.Label) : null);

        // vertex → nexus。出力は (anchorVertex@0, nexus@1)。
        // anchor のラベル絞り込みは ScanOp が行うため、追加の filter は不要。
        builder = new ExpandToNexusOp(builder, SourceVertexColumn: 0, pattern.Type, anchor.Role, Carry: null);
        varToColumn[anchor.Vertex.Variable] = 0;
        varToColumn[pattern.Variable]     = 1;
        entityKindOf[anchor.Vertex.Variable] = EntityKind.Vertex;
        entityKindOf[pattern.Variable]     = EntityKind.Nexus;

        // 残りのメンバーを nexus から順に展開する。ExpandMembersOp は
        // (nexus@0, member@1) + carry を放出するので、既存の束縛列を carry で持ち越し、
        // 展開後の新しい列位置へマップし直す。
        for (int i = 1; i < members.Count; i++)
        {
            var member = members[i];
            int nexusColumn = varToColumn[pattern.Variable];

            // 既に束縛済みの列を持ち越す (重複排除 + ソートで決定的にする)。
            int[] carry = new SortedSet<int>(varToColumn.Values).ToArray();

            builder = new ExpandMembersOp(
                builder,
                nexusColumn,
                member.Role,
                ExcludeVertexColumn: null,
                carry);

            // ExpandMembersOp 出力: nexus@0, member@1, carry[k] @ (2 + k)。
            var remapped = new Dictionary<string, int>(varToColumn.Count);
            foreach (var (variable, oldColumn) in varToColumn)
            {
                int idx = Array.IndexOf(carry, oldColumn);
                remapped[variable] = 2 + idx;
            }
            varToColumn = remapped;
            varToColumn[member.Vertex.Variable] = 1;
            entityKindOf[member.Vertex.Variable] = EntityKind.Vertex;

            // メンバーのラベル絞り込みは現在の member 列 (1) に適用する。
            if (member.Vertex.Label != null)
            {
                var labelId = schema.GetOrCreateLabel(member.Vertex.Label);
                builder = new FilterOp(builder, _ => new LabelPredicate(labelId, column: 1));
            }
        }

        builder = ApplyWherePredicates(builder, schema, varToColumn, entityKindOf, wherePredicates);
        return (PhysicalPlanner.Plan(builder, schema), varToColumn);
    }

    // 星型パターンの構築時検証。空 role、nexus/member 変数の重複を拒否する。
    // 未定義 variable は WHERE 適用時 (ApplyWherePredicates) に検出される。
    private static void ValidateStarPattern(NexusPattern pattern)
    {
        if (pattern.Members.Count == 0)
            throw new InvalidOperationException("星型Nexusパターンには少なくとも 1 つの Member が必要です。");

        var seen = new HashSet<string>(StringComparer.Ordinal) { pattern.Variable };
        foreach (var member in pattern.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Role))
                throw new InvalidOperationException("ロール名は空にできません。");
            if (!seen.Add(member.Vertex.Variable))
            {
                throw new InvalidOperationException(
                    $"パターン変数 '{member.Vertex.Variable}' が重複しています。"
                    + " Nexus変数とメンバー変数はすべて一意である必要があります。");
            }
        }
    }

    // WHERE 述語を対応する列へ適用する。<paramref name="entityKindOf"/> が指定された場合は
    // 変数のエンティティ種別に応じてNexus / Vertexのプロパティストアを切り替える。
    // null (binary パターン) のときは全変数をVertexとして扱う。
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
                             && kind == EntityKind.Nexus
                ? PredicateEntity.Nexus
                : PredicateEntity.Vertex;

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
