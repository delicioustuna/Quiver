using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

public sealed class PropertyOwnershipTests : IDisposable
{
    private readonly string[] _paths = Enumerable.Range(0, 4)
        .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        .ToArray();
    private readonly PagedFile[] _files;
    private readonly PropertyVersionStore _store;
    private static readonly EntityRef FirstOwner = EntityRef.From(VertexId.Create(10, 1));
    private static readonly EntityRef SecondOwner = EntityRef.From(VertexId.Create(11, 1));

    public PropertyOwnershipTests()
    {
        _files = _paths.Select(path => new PagedFile(path)).ToArray();
        _store = new PropertyVersionStore(_files[0], _files[1], _files[2], _files[3]);
    }

    [Fact]
    public void Create_rejects_a_chain_head_owned_by_another_entity()
    {
        PropertyVersionRef head = Create(FirstOwner, 1, PropertyCardinality.Single, 1);

        Action act = () => Create(SecondOwner, 2, PropertyCardinality.Single, 2, head);

        act.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void Single_and_set_versions_preserve_cardinality_and_previous_version()
    {
        PropertyVersionRef single = Create(FirstOwner, 1, PropertyCardinality.Single, 1);
        PropertyVersionRef updated = Create(FirstOwner, 1, PropertyCardinality.Single, 2, single);
        PropertyVersionRef setValue = Create(FirstOwner, 2, PropertyCardinality.Set, 3, updated);

        PropertyVersionRecord updatedRecord = _store.Read(FirstOwner, updated);
        PropertyVersionRecord setRecord = _store.Read(FirstOwner, setValue);
        updatedRecord.Cardinality.Should().Be(PropertyCardinality.Single);
        updatedRecord.PreviousVersion.Should().Be(single);
        setRecord.Cardinality.Should().Be(PropertyCardinality.Set);
    }

    [Fact]
    public void Delete_rejects_an_owner_that_does_not_match_the_version()
    {
        PropertyVersionRef version = Create(FirstOwner, 1, PropertyCardinality.Single, 1);

        Action act = () => _store.Delete(SecondOwner, version, version);

        act.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void Read_rejects_the_same_sequence_with_a_different_generation()
    {
        PropertyVersionRef version = Create(FirstOwner, 1, PropertyCardinality.Single, 1);
        var stale = PropertyVersionRef.Create(version.Sequence, version.Generation + 1);

        _store.Read(FirstOwner, stale).InUse.Should().BeFalse();
    }

    [Fact]
    public void Reusing_a_trailing_slot_after_reopen_advances_its_generation()
    {
        string[] paths = Enumerable.Range(0, 4)
            .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
            .ToArray();
        PropertyVersionRef original;
        try
        {
            using (var propertyFile = new PagedFile(paths[0]))
            using (var blobFile = new PagedFile(paths[1]))
            using (var vectorMetadataFile = new PagedFile(paths[2]))
            using (var vectorBlobFile = new PagedFile(paths[3]))
            {
                var store = new PropertyVersionStore(
                    propertyFile, blobFile, vectorMetadataFile, vectorBlobFile);
                PropertyValue value = PropertyValue.FromInt32(1);
                original = store.Create(
                    new PropertyAddress(FirstOwner, new PropertyKeyId(1)),
                    PropertyCardinality.Single,
                    in value,
                    PropertyVersionRef.Invalid);
                var plan = store.PlanVacuum(FirstOwner, original, true, long.MaxValue, new CommittedTxRegistry());
                plan.Reclaimed.Count.Should().Be(1);
                store.ApplyVacuum(plan).Should().Be(PropertyVersionRef.Invalid);
                store.FinishExternalReclaim();
                store.Read(FirstOwner, original).InUse.Should().BeFalse();
            }

            using var reopenedPropertyFile = new PagedFile(paths[0]);
            using var reopenedBlobFile = new PagedFile(paths[1]);
            using var reopenedVectorMetadataFile = new PagedFile(paths[2]);
            using var reopenedVectorBlobFile = new PagedFile(paths[3]);
            var reopened = new PropertyVersionStore(
                reopenedPropertyFile,
                reopenedBlobFile,
                reopenedVectorMetadataFile,
                reopenedVectorBlobFile);
            PropertyValue replacementValue = PropertyValue.FromInt32(2);

            PropertyVersionRef replacement = reopened.Create(
                new PropertyAddress(FirstOwner, new PropertyKeyId(1)),
                PropertyCardinality.Single,
                in replacementValue,
                PropertyVersionRef.Invalid);

            replacement.Sequence.Should().Be(original.Sequence);
            replacement.Generation.Should().Be(original.Generation + 1);
        }
        finally
        {
            foreach (string path in paths)
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("range")]
    public void Vacuum_planning_rejects_corrupt_chains_before_reclaiming_any_slot(string corruption)
    {
        var first = Create(FirstOwner, 1, PropertyCardinality.Single, 1, PropertyVersionRef.Invalid);
        var other = Create(SecondOwner, 2, PropertyCardinality.Single, 2, PropertyVersionRef.Invalid);
        var head = first;
        if (corruption == "generation") head = PropertyVersionRef.Create(first.Sequence, first.Generation + 1);
        else
        {
            using var page = _files[0].PinForWrite(new PageId(2));
            RecordHelpers.WriteInt48(page.Data[22..], corruption switch
            {
                "cycle" => first.Sequence,
                "owner" => other.Sequence,
                _ => 9999,
            });
        }
        byte[] before;
        using (var page = _files[0].PinForRead(new PageId(2))) before = page.Raw.ToArray();
        long freeHead = _store.FreeHead;
        Action plan = () => _store.PlanVacuum(FirstOwner, head, true, long.MaxValue,
            new CommittedTxRegistry());
        plan.Should().Throw<CorruptionException>();
        _store.FreeHead.Should().Be(freeHead);
        using var after = _files[0].PinForRead(new PageId(2));
        after.Raw.ToArray().Should().Equal(before);
    }

    [Fact]
    public void Vacuum_rejects_shared_vector_references_before_freeing_either_owner()
    {
        PropertyValue value = PropertyValue.FromFloatArray(new float[] { 1, 2, 3 });
        var first = _store.Create(new PropertyAddress(FirstOwner, new PropertyKeyId(1)),
            PropertyCardinality.Single, in value, PropertyVersionRef.Invalid);
        _store.Create(new PropertyAddress(SecondOwner, new PropertyKeyId(1)),
            PropertyCardinality.Single, in value, PropertyVersionRef.Invalid);
        byte[] before;
        using (var page = _files[0].PinForWrite(new PageId(2)))
        {
            page.Data.Slice(32, 12).CopyTo(page.Data.Slice(84 + 32, 12));
            before = page.Data.ToArray();
        }
        var plan = _store.PlanVacuum(FirstOwner, first, true, long.MaxValue, new CommittedTxRegistry());
        Action apply = () => _store.ApplyVacuum(plan);
        apply.Should().Throw<CorruptionException>().WithMessage("*shared*");
        using var after = _files[0].PinForRead(new PageId(2));
        after.Data.ToArray().Should().Equal(before);
        var vectors = new VectorPayloadStore(_files[2], _files[3]);
        vectors.Read(new VectorPayloadRef(0, 1)).Should().Equal(1, 2, 3);
        vectors.Read(new VectorPayloadRef(1, 1)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Applying_a_reclaim_plan_twice_rejects_the_stale_slot()
    {
        var first = Create(FirstOwner, 1, PropertyCardinality.Single, 1, PropertyVersionRef.Invalid);
        var plan = _store.PlanVacuum(FirstOwner, first, true, long.MaxValue, new CommittedTxRegistry());
        _store.ApplyVacuum(plan);
        long freeHead = _store.FreeHead;
        Action apply = () => _store.ApplyVacuum(plan);
        apply.Should().Throw<CorruptionException>();
        _store.FreeHead.Should().Be(freeHead);
    }

    public void Dispose()
    {
        foreach (PagedFile file in _files)
            file.Dispose();
        foreach (string path in _paths)
            File.Delete(path);
    }

    private PropertyVersionRef Create(
        EntityRef owner,
        int key,
        PropertyCardinality cardinality,
        int value,
        PropertyVersionRef head = default)
    {
        PropertyValue propertyValue = PropertyValue.FromInt32(value);
        return _store.Create(
            new PropertyAddress(owner, new PropertyKeyId(key)),
            cardinality,
            in propertyValue,
            head.IsValid ? head : PropertyVersionRef.Invalid);
    }
}
