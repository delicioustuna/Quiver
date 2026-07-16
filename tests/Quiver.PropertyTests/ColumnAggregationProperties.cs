using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.PropertyTests;

/// <summary>
/// 列スキャン集約の property-based 検証。任意の整数値列に対して
/// 「列スキャン集約 == row path 集約 == 素朴な合計/最大/最小」が常に成り立つことを確認する
/// (列==行の不変条件)。各ケースは独立した一時 DB で実行する。
/// </summary>
public class ColumnAggregationProperties
{
    [FsCheck.Xunit.Property(MaxTest = 60)]
    public Property Column_aggregation_equals_row_path_and_naive(int[]? input)
    {
        // グラフを小さく保つため値数を制限。空配列も有効ケース (合計 0)。
        var values = (input ?? Array.Empty<int>()).Take(40).Select(x => (long)x).ToArray();

        string dir = Path.Combine(Path.GetTempPath(), "quiver_colprop_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "graph.quiver");
        try
        {
            using var db = QuiverDatabase.Open(path);
            db.CreateColumn(EntityKind.Vertex, "v");
            using (var tx = db.BeginTransaction())
            {
                foreach (var v in values)
                {
                    var n = tx.CreateVertex("N");
                    tx.SetProperty(n, "v", PropertyValue.FromInt64(v));
                }
                tx.Commit();
            }

            long colSum; double? colMax; double? colMin;
            using (var tx = db.BeginReadOnlyTransaction())
            {
                var g = tx.G(db.Schema);
                colSum = g.Vertices().SumLong("v");
                colMax = g.Vertices().Max("v");
                colMin = g.Vertices().Min("v");
            }

            // 列を外して row path 経路で再計算。
            db.DropColumn(EntityKind.Vertex, "v");
            long rowSum; double? rowMax; double? rowMin;
            using (var tx = db.BeginReadOnlyTransaction())
            {
                var g = tx.G(db.Schema);
                rowSum = g.Vertices().SumLong("v");
                rowMax = g.Vertices().Max("v");
                rowMin = g.Vertices().Min("v");
            }

            long naiveSum = values.Sum();
            double? naiveMax = values.Length == 0 ? null : values.Max();
            double? naiveMin = values.Length == 0 ? null : values.Min();

            bool ok = colSum == rowSum && colSum == naiveSum
                   && colMax == rowMax && colMax == naiveMax
                   && colMin == rowMin && colMin == naiveMin;
            return ok.ToProperty();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
