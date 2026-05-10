using GraphDb.Engine.Core;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine;

public sealed class DegreeHistogram
{
    // Bucket upper-bounds: 0, 1, 3, 7, 15, 31, 63, 127, 255, ∞
    private readonly long[] _counts = new long[10];

    public long TotalNodes { get; private set; }
    public long TotalDegree { get; private set; }
    public long MaxDegree { get; private set; }
    public double MeanDegree => TotalNodes == 0 ? 0.0 : (double)TotalDegree / TotalNodes;
    public IReadOnlyList<long> BucketCounts => _counts;

    internal void Record(long degree)
    {
        TotalNodes++;
        TotalDegree += degree;
        if (degree > MaxDegree) MaxDegree = degree;
        _counts[BucketFor(degree)]++;
    }

    private static int BucketFor(long d) => d switch
    {
        0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 7 => 3,
        <= 15 => 4,
        <= 31 => 5,
        <= 63 => 6,
        <= 127 => 7,
        <= 255 => 8,
        _ => 9,
    };
}

public sealed class GraphStats
{
    public static readonly GraphStats Empty = new();

    public IReadOnlyDictionary<LabelId, long> LabelCardinality { get; private init; }
        = new Dictionary<LabelId, long>();
    public IReadOnlyDictionary<RelationshipTypeId, long> EdgeTypeFrequency { get; private init; }
        = new Dictionary<RelationshipTypeId, long>();
    public DegreeHistogram GlobalDegreeHistogram { get; private init; } = new();
    public IReadOnlyDictionary<LabelId, DegreeHistogram> DegreeByLabel { get; private init; }
        = new Dictionary<LabelId, DegreeHistogram>();
    public long TotalNodes { get; private init; }
    public long TotalRelationships { get; private init; }

    public long EstimateCardinality(LabelId label)
        => LabelCardinality.TryGetValue(label, out var n) ? n : 0;

    public double EstimateSelectivity(LabelId label)
        => TotalNodes == 0 ? 0.0 : (double)EstimateCardinality(label) / TotalNodes;

    public double EstimateMeanDegree(LabelId label)
        => DegreeByLabel.TryGetValue(label, out var h) ? h.MeanDegree : GlobalDegreeHistogram.MeanDegree;

    public static GraphStats Collect(ITransaction tx)
    {
        var labelCard   = new Dictionary<LabelId, long>();
        var edgeFreq    = new Dictionary<RelationshipTypeId, long>();
        var degreeByLabel = new Dictionary<LabelId, DegreeHistogram>();
        var globalHist  = new DegreeHistogram();
        long totalNodes = 0;
        long totalRels  = 0;

        foreach (var nodeId in tx.Nodes.Scan())
        {
            var node = tx.Nodes.Read(nodeId);
            if (!node.InUse) continue;

            totalNodes++;
            var label = node.Label;

            labelCard.TryGetValue(label, out var lc);
            labelCard[label] = lc + 1;

            if (!degreeByLabel.ContainsKey(label))
                degreeByLabel[label] = new DegreeHistogram();

            // Walk the full relationship chain (both directions) to compute degree.
            long nodeDegree = 0;
            var relId = node.FirstRelationshipId;
            while (relId.IsValid)
            {
                var rel = tx.Relationships.Read(relId);
                relId = rel.Source == nodeId ? rel.SourceNext : rel.TargetNext;

                // Count each edge once: from the source side.
                if (rel.Source == nodeId)
                {
                    totalRels++;
                    edgeFreq.TryGetValue(rel.Type, out var tc);
                    edgeFreq[rel.Type] = tc + 1;
                }

                nodeDegree++;
            }

            globalHist.Record(nodeDegree);
            degreeByLabel[label].Record(nodeDegree);
        }

        return new GraphStats
        {
            LabelCardinality   = labelCard,
            EdgeTypeFrequency  = edgeFreq,
            GlobalDegreeHistogram = globalHist,
            DegreeByLabel      = degreeByLabel,
            TotalNodes         = totalNodes,
            TotalRelationships = totalRels,
        };
    }
}
