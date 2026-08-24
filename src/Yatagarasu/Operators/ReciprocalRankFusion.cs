namespace Yatagarasu.Query.Physical;

internal readonly record struct ReciprocalRankFusionResult(
    long EntityId,
    double Score);

internal static class ReciprocalRankFusion
{
    internal const int RankConstant = 60;

    internal static List<ReciprocalRankFusionResult> Fuse(
        IReadOnlyList<IReadOnlyList<long>> rankedChannels,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedChannels);
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k));

        var scores = new Dictionary<long, double>();
        foreach (IReadOnlyList<long> channel in rankedChannels)
        {
            for (int i = 0; i < channel.Count; i++)
            {
                long entityId = channel[i];
                double contribution = 1.0 / (RankConstant + i + 1);
                scores[entityId] =
                    scores.GetValueOrDefault(entityId) + contribution;
            }
        }

        return scores
            .OrderByDescending(static item => item.Value)
            .ThenBy(static item => item.Key)
            .Take(k)
            .Select(static item => new ReciprocalRankFusionResult(
                item.Key,
                item.Value))
            .ToList();
    }
}
