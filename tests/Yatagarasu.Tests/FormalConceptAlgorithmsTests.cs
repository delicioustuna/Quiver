using Yatagarasu.Core;
using FluentAssertions;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class FormalConceptAlgorithmsTests
{
    [Fact]
    public void NextClosure_matches_independent_exhaustive_closure_oracle()
    {
        for (int seed = 0; seed < 4; seed++)
        {
            bool[,] incidence = RandomContext(8, 8, seed);
            using TestContext context = CreateContext(incidence);
            FormalConceptResult result = context.Read.EnumerateFormalConcepts(
                "Attribute", "object", Options(pageSize: 10_000));

            Signatures(result.Concepts).Should().BeEquivalentTo(ExhaustiveOracle(incidence));
            result.IsComplete.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void NextClosure_pages_have_no_duplicates_or_omissions(int pageSize)
    {
        bool[,] incidence = RandomContext(16, 12, 0x50414745);
        using TestContext context = CreateContext(incidence);
        string[] expected = EnumerateAll(context.Read, Options(pageSize: 10_000));
        string[] actual = EnumerateAll(context.Read, Options(pageSize));

        actual.Should().Equal(expected);
        actual.Distinct().Should().HaveCount(actual.Length);
    }

    [Fact]
    public void Continuation_rejects_filter_context_and_transaction_mismatches()
    {
        bool[,] incidence = RandomContext(8, 8, 17);
        using TestContext first = CreateContext(incidence);
        FormalConceptOptions options = Options(pageSize: 1);
        FormalConceptContinuation token = first.Read.EnumerateFormalConcepts(
            "Attribute", "object", options).Continuation!;

        Action filterMismatch = () => first.Read.EnumerateFormalConcepts(
            "Attribute", "object", options with { MinExtent = 1 }, token);
        filterMismatch.Should().Throw<ArgumentException>();

        Action contextMismatch = () => first.Read.EnumerateFormalConcepts(
            "Other", "object", options, token);
        contextMismatch.Should().Throw<ArgumentException>();

        var orderMismatchToken = new FormalConceptContinuation(
            token.Transaction, token.TransactionId, token.ContextFingerprint,
            token.ObjectOrderFingerprint, "different-order", token.OptionsFingerprint,
            token.Intent, token.Emitted, token.ClosureEvaluations, token.HasIntent);
        Action orderMismatch = () => first.Read.EnumerateFormalConcepts(
            "Attribute", "object", options, orderMismatchToken);
        orderMismatch.Should().Throw<InvalidOperationException>();

        var staleVersionToken = new FormalConceptContinuation(
            token.Transaction, token.TransactionId, token.ContextFingerprint,
            token.ObjectOrderFingerprint, token.AttributeOrderFingerprint, token.OptionsFingerprint,
            token.Intent, token.Emitted, token.ClosureEvaluations, token.HasIntent, version: 0);
        Action versionMismatch = () => first.Read.EnumerateFormalConcepts(
            "Attribute", "object", options, staleVersionToken);
        versionMismatch.Should().Throw<ArgumentException>();

        using TestContext second = CreateContext(incidence);
        Action transactionMismatch = () => second.Read.EnumerateFormalConcepts(
            "Attribute", "object", options, token);
        transactionMismatch.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Continuation_rejects_write_transaction_context_mutation()
    {
        using var database = YatagarasuDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        VertexId first = write.CreateVertex("Object");
        VertexId anchor1 = write.CreateVertex("Anchor");
        VertexId anchor2 = write.CreateVertex("Anchor");
        write.CreateNexus("Attribute",
            [new NexusMember("anchor", anchor1), new NexusMember("anchor", anchor2), new NexusMember("object", first)]);
        FormalConceptOptions options = Options(pageSize: 1);
        FormalConceptContinuation token = write.EnumerateFormalConcepts(
            "Attribute", "object", options).Continuation!;

        VertexId second = write.CreateVertex("Object");
        write.CreateNexus("Attribute",
            [new NexusMember("anchor", anchor1), new NexusMember("anchor", anchor2), new NexusMember("object", second)]);

        Action resume = () => write.EnumerateFormalConcepts("Attribute", "object", options, token);
        resume.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Contranominal_context_obeys_result_cap()
    {
        const int dimension = 12;
        var incidence = new bool[dimension, dimension];
        for (int obj = 0; obj < dimension; obj++)
            for (int attribute = 0; attribute < dimension; attribute++)
                incidence[obj, attribute] = obj != attribute;
        using TestContext context = CreateContext(incidence);

        FormalConceptResult result = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 137) with { MaxResults = 137 });

        result.Concepts.Should().HaveCount(137);
        result.TerminationReason.Should().Be(FormalConceptTerminationReason.MaxResultsReached);
        result.IsTruncated.Should().BeTrue();
        result.Continuation.Should().BeNull();
    }

    [Theory]
    [InlineData((byte)FormalConceptEnumerationStrategy.NextClosure)]
    [InlineData((byte)FormalConceptEnumerationStrategy.CloseByOne)]
    public void MaxResults_equal_to_total_is_completed_not_truncated(
        byte rawStrategy)
    {
        var strategy = (FormalConceptEnumerationStrategy)rawStrategy;
        bool[,] incidence = RandomContext(8, 8, 61);
        using TestContext context = CreateContext(incidence);
        int total = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(10_000)).Concepts.Count;
        FormalConceptOptions exact = Options(total) with
        {
            Strategy = strategy,
            MaxResults = total,
        };

        FormalConceptResult completed = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", exact);
        FormalConceptResult capped = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", exact with
            {
                PageSize = Math.Max(1, total - 1),
                MaxResults = Math.Max(1, total - 1),
            });

        completed.Concepts.Should().HaveCount(total);
        completed.TerminationReason.Should().Be(FormalConceptTerminationReason.Completed);
        completed.IsTruncated.Should().BeFalse();
        if (total > 1)
            capped.TerminationReason.Should().Be(FormalConceptTerminationReason.MaxResultsReached);
    }

    [Fact]
    public void Empty_and_singleton_contexts_have_their_mathematical_degenerate_concepts()
    {
        using var emptyDatabase = YatagarasuDatabase.CreateInMemory();
        using IReadTransaction emptyRead = emptyDatabase.BeginReadTransaction();
        FormalConceptResult empty = emptyRead.EnumerateFormalConcepts("Attribute", "object", Options(10));
        empty.IsComplete.Should().BeTrue();
        empty.Concepts.Should().ContainSingle();
        empty.Concepts[0].Extent.Should().BeEmpty();
        empty.Concepts[0].Intent.Should().BeEmpty();

        using TestContext singleton = CreateContext(new bool[,] { { true } });
        FormalConceptResult one = singleton.Read.EnumerateFormalConcepts("Attribute", "object", Options(10));
        one.IsComplete.Should().BeTrue();
        one.Concepts.Should().ContainSingle();
        one.Concepts[0].Extent.Should().ContainSingle();
        one.Concepts[0].Intent.Should().ContainSingle();
    }

    [Fact]
    public void Extent_and_intent_filters_include_the_exact_boundary()
    {
        bool[,] incidence = RandomContext(10, 8, 67);
        using TestContext context = CreateContext(incidence);
        FormalConceptResult all = context.Read.EnumerateFormalConcepts("Attribute", "object", Options(1_000));
        const int minExtent = 3;
        const int minIntent = 2;
        string[] expected = Signatures(all.Concepts
            .Where(concept => concept.Extent.Count >= minExtent && concept.Intent.Count >= minIntent)
            .ToArray());

        FormalConceptResult filtered = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(1_000) with { MinExtent = minExtent, MinIntent = minIntent });

        Signatures(filtered.Concepts).Should().Equal(expected);
        filtered.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Work_time_and_cancellation_are_explicit_and_bounded()
    {
        bool[,] incidence = RandomContext(64, 32, 29);
        using TestContext context = CreateContext(incidence);

        FormalConceptResult work = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 100) with { MaxClosureEvaluations = 1 });
        work.TerminationReason.Should().Be(FormalConceptTerminationReason.WorkBudget);
        work.IsComplete.Should().BeFalse();
        work.ClosureEvaluations.Should().Be(1);
        work.Continuation.Should().BeNull();

        FormalConceptResult timed = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 100) with { TimeLimit = TimeSpan.Zero });
        timed.TerminationReason.Should().Be(FormalConceptTerminationReason.TimeBudget);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        FormalConceptResult stopped = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 100) with { CancellationToken = cancelled.Token });
        stopped.TerminationReason.Should().Be(FormalConceptTerminationReason.Cancelled);
    }

    [Fact]
    public void Cancelled_resume_preserves_the_validated_position_for_a_fresh_token()
    {
        using TestContext context = CreateContext(RandomContext(16, 12, 73));
        FormalConceptOptions options = Options(1);
        FormalConceptContinuation token = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", options).Continuation!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        FormalConceptResult stopped = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", options with { CancellationToken = cancelled.Token }, token);
        FormalConceptResult resumed = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", options, stopped.Continuation);

        stopped.TerminationReason.Should().Be(FormalConceptTerminationReason.Cancelled);
        stopped.Continuation.Should().BeSameAs(token);
        resumed.Concepts.Should().ContainSingle();
    }

    [Fact]
    public void Time_budget_returns_a_resumable_prefix_after_context_materialization()
    {
        const int dimension = 24;
        var incidence = new bool[dimension, dimension];
        for (int obj = 0; obj < dimension; obj++)
            for (int attribute = 0; attribute < dimension; attribute++)
                incidence[obj, attribute] = obj != attribute;
        using TestContext context = CreateContext(incidence);
        FormalConceptOptions options = Options(100_000) with
        {
            MinExtent = dimension - 1,
            MaxResults = 100_000,
            MaxClosureEvaluations = long.MaxValue,
            TimeLimit = TimeSpan.FromMilliseconds(500),
        };

        FormalConceptResult timed = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", options);

        timed.TerminationReason.Should().Be(FormalConceptTerminationReason.TimeBudget);
        timed.Concepts.Should().NotBeEmpty();
        timed.Continuation.Should().NotBeNull();
    }

    [Theory]
    [InlineData((byte)FormalConceptEnumerationStrategy.NextClosure)]
    [InlineData((byte)FormalConceptEnumerationStrategy.CloseByOne)]
    public void One_closure_budget_finishes_exactly_one_closure_and_is_terminal(
        byte rawStrategy)
    {
        var strategy = (FormalConceptEnumerationStrategy)rawStrategy;
        using TestContext context = CreateContext(RandomContext(16, 12, 71));
        FormalConceptOptions options = Options(100_000) with
        {
            Strategy = strategy,
            MaxClosureEvaluations = 1,
        };

        FormalConceptResult result = context.Read.EnumerateFormalConcepts("Attribute", "object", options);

        result.ClosureEvaluations.Should().Be(1);
        result.TerminationReason.Should().Be(FormalConceptTerminationReason.WorkBudget);
        result.Continuation.Should().BeNull();
    }

    [Theory]
    [InlineData((byte)FormalConceptEnumerationStrategy.NextClosure)]
    [InlineData((byte)FormalConceptEnumerationStrategy.CloseByOne)]
    public void One_closure_budget_completes_when_root_is_the_final_concept(
        byte rawStrategy)
    {
        var strategy = (FormalConceptEnumerationStrategy)rawStrategy;
        using TestContext context = CreateContext(new bool[,] { { true, true } });
        FormalConceptOptions options = Options(10) with
        {
            Strategy = strategy,
            MaxResults = 10,
            MaxClosureEvaluations = 1,
        };

        FormalConceptResult result = context.Read.EnumerateFormalConcepts("Attribute", "object", options);

        result.ClosureEvaluations.Should().Be(1);
        result.TerminationReason.Should().Be(FormalConceptTerminationReason.Completed);
        result.Continuation.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Input_caps_return_no_ambiguous_partial_concepts(int cap)
    {
        bool[,] incidence = RandomContext(8, 8, 53);
        using TestContext context = CreateContext(incidence);
        FormalConceptOptions options = cap switch
        {
            0 => Options(100) with { MaxObjects = 1 },
            1 => Options(100) with { MaxAttributes = 1 },
            _ => Options(100) with { MaxIncidences = 1 },
        };

        FormalConceptResult result = context.Read.EnumerateFormalConcepts("Attribute", "object", options);

        result.Concepts.Should().BeEmpty();
        result.Continuation.Should().BeNull();
        result.ContextFingerprint.Should().BeNull();
        result.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Long_read_continuation_keeps_its_original_snapshot_after_later_commit()
    {
        using var database = YatagarasuDatabase.CreateInMemory();
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            VertexId a = seed.CreateVertex("Object");
            VertexId b = seed.CreateVertex("Object");
            seed.CreateNexus("Attribute", [new NexusMember("object", a), new NexusMember("object", b)]);
            seed.Commit();
        }
        using IReadTransaction read = database.BeginReadTransaction();
        FormalConceptOptions options = Options(1);
        FormalConceptContinuation token = read.EnumerateFormalConcepts("Attribute", "object", options).Continuation!;
        using (IWriteTransaction later = database.BeginWriteTransaction())
        {
            VertexId a = later.CreateVertex("Object");
            VertexId b = later.CreateVertex("Object");
            later.CreateNexus("Attribute", [new NexusMember("object", a), new NexusMember("object", b)]);
            later.Commit();
        }

        Action resume = () => read.EnumerateFormalConcepts("Attribute", "object", options, token);
        resume.Should().NotThrow();
    }

    [Fact]
    public void Word_bitmap_supports_more_than_sixty_four_attributes_and_returns_packed_order()
    {
        var incidence = new bool[3, 130];
        for (int attribute = 0; attribute < 130; attribute++) incidence[0, attribute] = true;
        for (int attribute = 64; attribute < 130; attribute++) incidence[1, attribute] = true;
        incidence[2, 129] = true;
        using TestContext context = CreateContext(incidence);

        FormalConceptResult result = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 100));

        result.IsComplete.Should().BeTrue();
        result.AttributeCount.Should().Be(130);
        foreach (FormalConcept concept in result.Concepts)
        {
            concept.Extent.Select(Packed).Should().BeInAscendingOrder();
            concept.Intent.Select(Packed).Should().BeInAscendingOrder();
        }
    }

    [Fact]
    public void CloseByOne_and_NextClosure_agree()
    {
        bool[,] incidence = RandomContext(12, 10, 41);
        using TestContext context = CreateContext(incidence);
        FormalConceptResult next = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 10_000));
        FormalConceptResult cbo = context.Read.EnumerateFormalConcepts(
            "Attribute", "object", Options(pageSize: 10_000) with
            {
                Strategy = FormalConceptEnumerationStrategy.CloseByOne,
                MaxResults = 10_000,
            });

        Signatures(cbo.Concepts).Should().BeEquivalentTo(Signatures(next.Concepts));
    }

    [Fact]
    public void Repeated_object_attribute_membership_is_deduplicated()
    {
        using var database = YatagarasuDatabase.CreateInMemory();
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            VertexId repeated = write.CreateVertex("Object");
            VertexId other = write.CreateVertex("Object");
            write.CreateNexus("Attribute",
            [
                new NexusMember("first", repeated),
                new NexusMember("second", repeated),
                new NexusMember("third", other),
            ]);
            write.Commit();
        }
        using IReadTransaction read = database.BeginReadTransaction();

        FormalConceptResult result = read.EnumerateFormalConcepts("Attribute", null, Options(10));

        result.ObjectCount.Should().Be(2);
        result.AttributeCount.Should().Be(1);
        result.IncidenceCount.Should().Be(2);
    }

    private static FormalConceptOptions Options(int pageSize) => new()
    {
        Strategy = FormalConceptEnumerationStrategy.NextClosure,
        PageSize = pageSize,
        MaxResults = 100_000,
        MaxClosureEvaluations = 1_000_000,
        MaxObjects = 1_000,
        MaxAttributes = 4_096,
        MaxIncidences = 100_000,
        TimeLimit = TimeSpan.FromSeconds(5),
    };

    private static string[] EnumerateAll(IReadTransaction read, FormalConceptOptions options)
    {
        var result = new List<string>();
        FormalConceptContinuation? continuation = null;
        do
        {
            FormalConceptResult page = read.EnumerateFormalConcepts(
                "Attribute", "object", options, continuation);
            result.AddRange(Signatures(page.Concepts));
            continuation = page.Continuation;
        }
        while (continuation is not null);
        return result.ToArray();
    }

    private static HashSet<string> ExhaustiveOracle(bool[,] incidence)
    {
        int objects = incidence.GetLength(0);
        int attributes = incidence.GetLength(1);
        var concepts = new HashSet<string>();
        for (int seed = 0; seed < 1 << attributes; seed++)
        {
            var extent = new List<int>();
            for (int obj = 0; obj < objects; obj++)
            {
                bool contains = true;
                for (int attribute = 0; attribute < attributes; attribute++)
                    if ((seed & 1 << attribute) != 0 && !incidence[obj, attribute]) contains = false;
                if (contains) extent.Add(obj);
            }
            var intent = new List<int>();
            for (int attribute = 0; attribute < attributes; attribute++)
                if (extent.All(obj => incidence[obj, attribute])) intent.Add(attribute);
            var closedExtent = Enumerable.Range(0, objects)
                .Where(obj => intent.All(attribute => incidence[obj, attribute]));
            concepts.Add($"{string.Join(',', closedExtent)}|{string.Join(',', intent)}");
        }
        return concepts;
    }

    private static string[] Signatures(IReadOnlyList<FormalConcept> concepts) => concepts
        .Select(concept => $"{string.Join(',', concept.Extent.Select(x => x.Sequence))}|" +
            $"{string.Join(',', concept.Intent.Select(x => x.Sequence))}")
        .ToArray();

    private static bool[,] RandomContext(int objects, int attributes, int seed)
    {
        var random = new Random(seed);
        var result = new bool[objects, attributes];
        for (int obj = 0; obj < objects; obj++)
            for (int attribute = 0; attribute < attributes; attribute++)
                result[obj, attribute] = random.NextDouble() < 0.5;
        return result;
    }

    private static TestContext CreateContext(bool[,] incidence)
    {
        var database = YatagarasuDatabase.CreateInMemory();
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            VertexId[] objects = Enumerable.Range(0, incidence.GetLength(0))
                .Select(_ => write.CreateVertex("Object")).ToArray();
            VertexId anchor1 = write.CreateVertex("Anchor");
            VertexId anchor2 = write.CreateVertex("Anchor");
            for (int attribute = 0; attribute < incidence.GetLength(1); attribute++)
            {
                NexusMember[] members =
                [
                    new NexusMember("anchor", anchor1),
                    new NexusMember("anchor", anchor2),
                    .. Enumerable.Range(0, objects.Length)
                    .Where(obj => incidence[obj, attribute])
                    .Select(obj => new NexusMember("object", objects[obj]))
                ];
                write.CreateNexus("Attribute", members);
            }
            write.Commit();
        }
        return new(database, database.BeginReadTransaction());
    }

    private static long Packed(EntityRef entity) => EntityRef.Pack(entity.Kind, entity.Sequence, entity.Generation);

    private sealed record TestContext(YatagarasuDatabase Database, IReadTransaction Read) : IDisposable
    {
        public void Dispose()
        {
            Read.Dispose();
            Database.Dispose();
        }
    }
}
