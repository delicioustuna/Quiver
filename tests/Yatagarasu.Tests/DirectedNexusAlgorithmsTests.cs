using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class DirectedNexusAlgorithmsTests
{
    [Fact]
    public void Reachability_requires_every_tail_and_handles_cycles_self_loops_and_multiple_seeds()
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        VertexId a, b, c, d, e, unreachable;
        using (var write = db.BeginWriteTransaction())
        {
            a = write.CreateVertex("Item");
            b = write.CreateVertex("Item");
            c = write.CreateVertex("Item");
            d = write.CreateVertex("Item");
            e = write.CreateVertex("Item");
            unreachable = write.CreateVertex("Item");
            write.CreateNexus("Rule", [new("tail", a), new("tail", b), new("head", c)]);
            write.CreateNexus("Rule", [new("tail", c), new("head", d)]);
            write.CreateNexus("Rule", [new("tail", d), new("head", d)]);
            write.CreateNexus("Rule", [new("tail", d), new("tail", e), new("head", a)]);
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        var partial = read.FindReachableVertices([a], "Rule", "tail", "head");
        partial.Vertices.Should().BeEquivalentTo([a]);

        var complete = read.FindReachableVertices([a, b, e], "Rule", "tail", "head");
        complete.IsComplete.Should().BeTrue();
        complete.Vertices.Should().BeEquivalentTo([a, b, c, d, e]);
        complete.Vertices.Should().NotContain(unreachable);
    }

    [Fact]
    public void Reachability_is_bound_to_the_read_snapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_directed_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            using var db = YatagarasuDatabase.Open(path);
            VertexId seed, head;
            using (var write = db.BeginWriteTransaction())
            {
                seed = write.CreateVertex("Item");
                head = write.CreateVertex("Item");
                write.Commit();
            }
            using var oldRead = db.BeginReadTransaction();
            using (var write = db.BeginWriteTransaction())
            {
                NexusId nexus = write.CreateNexus("Rule", [new("tail", seed), new("head", head)]);
                write.SetProperty(nexus, "cost", PropertyValue.FromDouble(1));
                write.Commit();
            }

            oldRead.FindReachableVertices([seed], "Rule", "tail", "head").Vertices
                .Should().BeEquivalentTo([seed]);
            oldRead.FindShortestDerivation(
                    [seed], head, "Rule", "tail", "head",
                    static (tx, nexus) => tx.GetProperty(nexus, "cost").DoubleValue,
                    DerivationCostMode.Additive)
                .IsReachable.Should().BeFalse();
            using var newRead = db.BeginReadTransaction();
            newRead.FindReachableVertices([seed], "Rule", "tail", "head").Vertices
                .Should().BeEquivalentTo([seed, head]);
            newRead.FindShortestDerivation(
                    [seed], head, "Rule", "tail", "head",
                    static (tx, nexus) => tx.GetProperty(nexus, "cost").DoubleValue,
                    DerivationCostMode.Additive)
                .Cost.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Reachability_matches_an_independent_fixed_point_oracle()
    {
        var random = new Random(0x42524541);
        for (int trial = 0; trial < 24; trial++)
        {
            using var db = YatagarasuDatabase.CreateInMemory();
            var arcs = new List<ReachArc>();
            VertexId[] vertices;
            using (var write = db.BeginWriteTransaction())
            {
                vertices = Enumerable.Range(0, 8).Select(_ => write.CreateVertex("Item")).ToArray();
                for (int i = 0; i < 14; i++)
                {
                    VertexId[] tails = vertices.OrderBy(_ => random.Next()).Take(1 + random.Next(3)).ToArray();
                    VertexId head = vertices[random.Next(vertices.Length)];
                    write.CreateNexus("Rule",
                        tails.Select(tail => new NexusMember("tail", tail)).Append(new("head", head)).ToArray());
                    arcs.Add(new(tails, head));
                }
                write.Commit();
            }

            VertexId[] seeds = [vertices[0], vertices[1]];
            HashSet<VertexId> expected = FixedPoint(arcs, seeds);
            using var read = db.BeginReadTransaction();
            var actual = read.FindReachableVertices(seeds, "Rule", "tail", "head");
            actual.IsComplete.Should().BeTrue();
            actual.Vertices.Should().BeEquivalentTo(expected);
        }
    }

    [Fact]
    public void Shortest_derivation_implements_additive_bottleneck_ties_and_tree_occurrences()
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        VertexId seed, shared, left, right, target, unreachable;
        var costs = new Dictionary<NexusId, double>();
        NexusId tiedFirst, tiedSecond;
        using (var write = db.BeginWriteTransaction())
        {
            seed = write.CreateVertex("Item");
            shared = write.CreateVertex("Item");
            left = write.CreateVertex("Item");
            right = write.CreateVertex("Item");
            target = write.CreateVertex("Item");
            unreachable = write.CreateVertex("Item");
            Add(write, costs, 2.5, [new("tail", seed), new("head", shared)]);
            Add(write, costs, 0, [new("tail", shared), new("head", left)]);
            Add(write, costs, 0, [new("tail", shared), new("head", right)]);
            Add(write, costs, 10, [new("tail", left), new("tail", right), new("head", target)]);
            tiedFirst = Add(write, costs, 3, [new("tail", seed), new("head", unreachable)]);
            tiedSecond = Add(write, costs, 3, [new("tail", seed), new("head", unreachable)]);
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        double Cost(IReadTransaction _, NexusId id) => costs[id];
        var additive = read.FindShortestDerivation(
            [seed], target, "Rule", "tail", "head", Cost, DerivationCostMode.Additive);
        var bottleneck = read.FindShortestDerivation(
            [seed], target, "Rule", "tail", "head", Cost, DerivationCostMode.Bottleneck);

        additive.Cost.Should().Be(15);
        bottleneck.Cost.Should().Be(10);
        Evaluate(read, additive.Tree, costs, DerivationCostMode.Additive).Should().Be(additive.Cost);
        Evaluate(read, bottleneck.Tree, costs, DerivationCostMode.Bottleneck).Should().Be(bottleneck.Cost);
        additive.Tree.Count(node => node.VertexId == shared).Should().Be(2);

        var tie = read.FindShortestDerivation(
            [seed], unreachable, "Rule", "tail", "head", Cost, DerivationCostMode.Additive);
        tie.Tree[0].NexusId.Should().Be(tiedFirst.Value < tiedSecond.Value ? tiedFirst : tiedSecond);
        var repeatedTie = read.FindShortestDerivation(
            [seed], unreachable, "Rule", "tail", "head", Cost, DerivationCostMode.Additive);
        repeatedTie.Tree.Should().Equal(tie.Tree);
    }

    [Fact]
    public void Shortest_derivation_matches_an_independent_relaxation_oracle()
    {
        var random = new Random(0x4252);
        for (int trial = 0; trial < 24; trial++)
        {
            using var db = YatagarasuDatabase.CreateInMemory();
            var arcs = new List<Arc>();
            var costs = new Dictionary<NexusId, double>();
            VertexId[] vertices;
            using (var write = db.BeginWriteTransaction())
            {
                vertices = Enumerable.Range(0, 8).Select(_ => write.CreateVertex("Item")).ToArray();
                for (int i = 0; i < 14; i++)
                {
                    VertexId[] tails = vertices.OrderBy(_ => random.Next()).Take(1 + random.Next(3)).ToArray();
                    VertexId head = vertices[random.Next(vertices.Length)];
                    double cost = random.Next(5);
                    NexusId id = Add(write, costs, cost,
                        tails.Select(tail => new NexusMember("tail", tail)).Append(new("head", head)).ToArray());
                    arcs.Add(new(tails, head, cost, id));
                }
                write.Commit();
            }
            VertexId[] seeds = [vertices[0], vertices[1]];
            using var read = db.BeginReadTransaction();
            foreach (DerivationCostMode mode in Enum.GetValues<DerivationCostMode>())
            {
                double expected = Oracle(vertices, arcs, seeds, vertices[^1], mode);
                var actual = read.FindShortestDerivation(
                    seeds, vertices[^1], "Rule", "tail", "head", (_, id) => costs[id], mode);
                actual.IsReachable.Should().Be(double.IsFinite(expected));
                actual.Cost.Should().Be(double.IsFinite(expected) ? expected : null);
                if (actual.IsReachable)
                    Evaluate(read, actual.Tree, costs, mode).Should().Be(actual.Cost);
            }
        }
    }

    [Fact]
    public void Shortest_derivation_returns_when_the_target_is_finalized()
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        VertexId seed, target, after;
        var costs = new Dictionary<NexusId, double>();
        using (var write = db.BeginWriteTransaction())
        {
            seed = write.CreateVertex("Item");
            target = write.CreateVertex("Item");
            after = write.CreateVertex("Item");
            Add(write, costs, 1, [new("tail", seed), new("head", target)]);
            Add(write, costs, 1, [new("tail", target), new("head", after)]);
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        double Cost(IReadTransaction _, NexusId id) => costs[id];
        var reachedTarget = read.FindShortestDerivation(
            [seed], target, "Rule", "tail", "head", Cost, DerivationCostMode.Additive,
            new() { MaxNexuses = 1 });
        reachedTarget.IsComplete.Should().BeTrue();
        reachedTarget.IsReachable.Should().BeTrue();
        reachedTarget.Cost.Should().Be(1);
        reachedTarget.VisitedNexusCount.Should().Be(1);

        var targetIsSeed = read.FindShortestDerivation(
            [target], target, "Rule", "tail", "head", Cost, DerivationCostMode.Additive,
            new() { MaxNexuses = 1 });
        targetIsSeed.IsComplete.Should().BeTrue();
        targetIsSeed.Cost.Should().Be(0);
        targetIsSeed.Tree.Should().Equal(new DerivationTreeNode(target, NexusId.Invalid, -1));
        targetIsSeed.VisitedNexusCount.Should().Be(0);
    }

    [Fact]
    public void Explicit_limits_and_cancellation_are_reported_or_thrown()
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        VertexId a, b;
        NexusId nexus;
        using (var write = db.BeginWriteTransaction())
        {
            a = write.CreateVertex("Item");
            b = write.CreateVertex("Item");
            nexus = write.CreateNexus("Rule", [new("tail", a), new("head", b)]);
            write.Commit();
        }
        using var read = db.BeginReadTransaction();
        var limited = read.FindReachableVertices([a], "Rule", "tail", "head",
            new() { MaxResults = 1 });
        limited.IsComplete.Should().BeFalse();
        limited.TerminationReason.Should().Be(DirectedNexusTerminationReason.MaxResultsReached);

        var treeLimited = read.FindShortestDerivation(
            [a], b, "Rule", "tail", "head", (_, id) => id == nexus ? 1 : 0,
            DerivationCostMode.Additive, new() { MaxTreeNodes = 1 });
        treeLimited.IsReachable.Should().BeTrue();
        treeLimited.TerminationReason.Should().Be(DirectedNexusTerminationReason.MaxTreeNodesReached);
        treeLimited.Tree.Should().BeEmpty();

        Action invalidCost = () => read.FindShortestDerivation(
            [a], b, "Rule", "tail", "head", (_, _) => double.NaN, DerivationCostMode.Additive);
        invalidCost.Should().Throw<ArgumentOutOfRangeException>();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Action act = () => read.FindReachableVertices([a], "Rule", "tail", "head",
            new() { CancellationToken = cancelled.Token });
        act.Should().Throw<OperationCanceledException>();
    }

    private static NexusId Add(IWriteTransaction write, Dictionary<NexusId, double> costs, double cost, NexusMember[] members)
    {
        NexusId id = write.CreateNexus("Rule", members);
        write.SetProperty(id, "cost", PropertyValue.FromDouble(cost));
        costs[id] = cost;
        return id;
    }

    private static double Evaluate(
        IReadTransaction transaction,
        IReadOnlyList<DerivationTreeNode> tree,
        IReadOnlyDictionary<NexusId, double> costs,
        DerivationCostMode mode)
    {
        var values = new double[tree.Count];
        var childValues = new List<double>[tree.Count];
        var childVertices = new List<VertexId>[tree.Count];
        for (int i = 0; i < tree.Count; i++)
        {
            childValues[i] = [];
            childVertices[i] = [];
        }
        for (int i = tree.Count - 1; i >= 0; i--)
        {
            DerivationTreeNode node = tree[i];
            if (node.NexusId == NexusId.Invalid)
            {
                values[i] = 0;
            }
            else
            {
                var expectedTails = new List<VertexId>();
                var members = transaction.GetMembers(node.NexusId, "tail");
                while (members.MoveNext()) expectedTails.Add(members.Current.VertexId);
                members.Dispose();
                childVertices[i].Should().BeEquivalentTo(expectedTails);
                values[i] = mode == DerivationCostMode.Additive
                    ? costs[node.NexusId] + childValues[i].Sum()
                    : Math.Max(costs[node.NexusId], childValues[i].DefaultIfEmpty().Max());
            }
            if (node.ParentIndex >= 0)
            {
                childValues[node.ParentIndex].Add(values[i]);
                childVertices[node.ParentIndex].Add(node.VertexId);
            }
        }
        return values[0];
    }

    private static double Oracle(
        VertexId[] vertices,
        IReadOnlyList<Arc> arcs,
        IReadOnlyList<VertexId> seeds,
        VertexId target,
        DerivationCostMode mode)
    {
        var distance = vertices.ToDictionary(v => v, _ => double.PositiveInfinity);
        foreach (VertexId seed in seeds) distance[seed] = 0;
        for (int pass = 0; pass < vertices.Length; pass++)
        {
            var next = new Dictionary<VertexId, double>(distance);
            foreach (Arc arc in arcs)
            {
                if (arc.Tails.Any(t => !double.IsFinite(distance[t]))) continue;
                double aggregate = mode == DerivationCostMode.Additive
                    ? arc.Tails.Sum(t => distance[t])
                    : arc.Tails.Max(t => distance[t]);
                double candidate = mode == DerivationCostMode.Additive
                    ? aggregate + arc.Cost
                    : Math.Max(aggregate, arc.Cost);
                if (candidate < next[arc.Head]) next[arc.Head] = candidate;
            }
            if (next.All(pair => pair.Value == distance[pair.Key])) return distance[target];
            distance = next;
        }
        return distance[target];
    }

    private static HashSet<VertexId> FixedPoint(IReadOnlyList<ReachArc> arcs, IEnumerable<VertexId> seeds)
    {
        var reached = new HashSet<VertexId>(seeds);
        bool changed;
        do
        {
            changed = false;
            foreach (ReachArc arc in arcs)
            {
                if (reached.Contains(arc.Head) || arc.Tails.Any(tail => !reached.Contains(tail))) continue;
                changed |= reached.Add(arc.Head);
            }
        }
        while (changed);
        return reached;
    }

    private sealed record Arc(VertexId[] Tails, VertexId Head, double Cost, NexusId Id);
    private sealed record ReachArc(VertexId[] Tails, VertexId Head);
}
