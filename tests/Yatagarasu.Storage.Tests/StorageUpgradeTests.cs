using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Storage.Upgrade;
using Xunit;

namespace Yatagarasu.Storage.Tests;

public sealed class StorageUpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "yatagarasu-storage-upgrade-" + Guid.NewGuid().ToString("N"));

    public StorageUpgradeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Current_database_returns_already_current_without_writing_files()
    {
        string sourcePath = CreateCurrentDatabase("current.yata");
        byte[] before = File.ReadAllBytes(sourcePath);
        DateTime lastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);

        StorageUpgradeResult result = YatagarasuDatabase.UpgradeStorage(sourcePath);

        result.Status.Should().Be(StorageUpgradeStatus.AlreadyCurrent);
        result.SourceVersion.Should().Be(StorageFormatVersion.Current);
        result.TargetVersion.Should().Be(StorageFormatVersion.Current);
        result.BackupFilePath.Should().BeNull();
        File.ReadAllBytes(sourcePath).Should().Equal(before);
        File.GetLastWriteTimeUtc(sourcePath).Should().Be(lastWriteBefore);
        File.Exists(StorageUpgradeOrchestrator.GetMarkerPath(sourcePath)).Should().BeFalse();
    }

    [Fact]
    public void Unsupported_source_reports_both_versions_and_preserves_source()
    {
        string sourcePath = CreateVersionOneFixture("unsupported.yata");
        byte[] before = File.ReadAllBytes(sourcePath);

        Action act = () => YatagarasuDatabase.UpgradeStorage(sourcePath);

        StorageUpgradeNotSupportedException exception = act.Should()
            .Throw<StorageUpgradeNotSupportedException>()
            .Which;
        exception.SourceVersion.Should().Be(1);
        exception.TargetVersion.Should().Be(StorageFormatVersion.Current);
        File.ReadAllBytes(sourcePath).Should().Equal(before);
        File.Exists(StorageUpgradeOrchestrator.GetMarkerPath(sourcePath)).Should().BeFalse();
    }

    [Fact]
    public void Upgrade_requires_the_database_to_be_closed()
    {
        string sourcePath = CreateCurrentDatabase("open.yata");
        using var database = YatagarasuDatabase.Open(sourcePath);

        Action act = () => YatagarasuDatabase.UpgradeStorage(sourcePath);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Truncated_header_is_rejected_before_any_upgrade_step_runs()
    {
        string sourcePath = Path.Combine(_directory, "truncated.yata");
        File.WriteAllBytes(sourcePath, "QUIVER"u8.ToArray());

        Action act = () => YatagarasuDatabase.UpgradeStorage(sourcePath);

        act.Should().Throw<CorruptionException>();
        File.ReadAllBytes(sourcePath).Should().Equal("QUIVER"u8.ToArray());
    }

    [Fact]
    public void Current_header_with_invalid_checksum_is_rejected_without_writing()
    {
        string sourcePath = CreateCurrentDatabase("bad-checksum.yata");
        using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 100;
            int original = stream.ReadByte();
            stream.Position = 100;
            stream.WriteByte((byte)(original ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }
        byte[] corrupted = File.ReadAllBytes(sourcePath);

        Action act = () => YatagarasuDatabase.UpgradeStorage(sourcePath);

        act.Should().Throw<CorruptionException>();
        File.ReadAllBytes(sourcePath).Should().Equal(corrupted);
    }

    [Theory]
    [InlineData((int)StorageUpgradeBoundary.TargetValidated)]
    [InlineData((int)StorageUpgradeBoundary.MarkerPersisted)]
    [InlineData((int)StorageUpgradeBoundary.SourceMoved)]
    [InlineData((int)StorageUpgradeBoundary.TargetMoved)]
    public void Injected_failure_never_loses_source_and_retry_converges(int boundaryValue)
    {
        var failureBoundary = (StorageUpgradeBoundary)boundaryValue;
        string sourcePath = CreateVersionOneFixture($"fault-{failureBoundary}.yata");
        byte[] original = File.ReadAllBytes(sourcePath);
        var step = new TestUpgradeStep();
        var fault = new ThrowAtBoundary(failureBoundary);

        Action interrupted = () => StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [step],
            fault);

        interrupted.Should().Throw<InjectedFailureException>();
        AssertSourceOrTargetIsAuthoritative(sourcePath, original);

        StorageUpgradeResult resumed = StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [step],
            faultInjector: null);

        resumed.Status.Should().Be(StorageUpgradeStatus.Upgraded);
        resumed.SourceVersion.Should().Be(1);
        resumed.TargetVersion.Should().Be(StorageFormatVersion.Current);
        resumed.BackupFilePath.Should().NotBeNull();
        File.ReadAllBytes(resumed.BackupFilePath!).Should().Equal(original);
        File.Exists(StorageUpgradeOrchestrator.GetMarkerPath(sourcePath)).Should().BeFalse();

        using var database = YatagarasuDatabase.Open(sourcePath);
        database.Diagnostics.GetStatistics().VertexCount.Should().Be(1);
    }

    [Fact]
    public void Successful_upgrade_can_remove_the_rollback_copy()
    {
        string sourcePath = CreateVersionOneFixture("without-backup.yata");

        StorageUpgradeResult result = StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions { KeepBackup = false },
            [new TestUpgradeStep()],
            faultInjector: null);

        result.Status.Should().Be(StorageUpgradeStatus.Upgraded);
        result.BackupFilePath.Should().BeNull();
        Directory.GetFiles(_directory, "*.upgrade-rollback").Should().BeEmpty();
        StorageFormatInspector.Inspect(sourcePath).Should().Be(StorageFormatVersion.Current);
    }

    [Fact]
    public void Missing_validated_target_restores_the_original_source()
    {
        string sourcePath = CreateVersionOneFixture("lost-target.yata");
        byte[] original = File.ReadAllBytes(sourcePath);

        Action interrupted = () => StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [new TestUpgradeStep()],
            new ThrowAtBoundary(StorageUpgradeBoundary.SourceMoved));
        interrupted.Should().Throw<InjectedFailureException>();

        string targetPath = Directory.GetFiles(_directory, "*.upgrade-target").Single();
        File.Delete(targetPath);

        Action resume = () => StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [new TestUpgradeStep()],
            faultInjector: null);

        resume.Should().Throw<StorageException>();
        File.ReadAllBytes(sourcePath).Should().Equal(original);
        File.Exists(StorageUpgradeOrchestrator.GetMarkerPath(sourcePath)).Should().BeFalse();
    }

    [Theory]
    [InlineData((int)StorageUpgradeBoundary.MarkerPersisted)]
    [InlineData((int)StorageUpgradeBoundary.SourceMoved)]
    public void Corrupted_validated_target_never_replaces_the_original_source(int boundaryValue)
    {
        var failureBoundary = (StorageUpgradeBoundary)boundaryValue;
        string sourcePath = CreateVersionOneFixture($"corrupt-target-{failureBoundary}.yata");
        byte[] original = File.ReadAllBytes(sourcePath);

        Action interrupted = () => StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [new TestUpgradeStep()],
            new ThrowAtBoundary(failureBoundary));
        interrupted.Should().Throw<InjectedFailureException>();

        string targetPath = Directory.GetFiles(_directory, "*.upgrade-target").Single();
        using (var stream = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 100;
            int originalByte = stream.ReadByte();
            stream.Position = 100;
            stream.WriteByte((byte)(originalByte ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }

        Action resume = () => StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [new TestUpgradeStep()],
            faultInjector: null);

        resume.Should().Throw<StorageException>();
        File.ReadAllBytes(sourcePath).Should().Equal(original);
        File.Exists(StorageUpgradeOrchestrator.GetMarkerPath(sourcePath)).Should().BeFalse();
    }

    [Fact]
    public void Failed_step_removes_its_partial_target_and_preserves_source()
    {
        string sourcePath = CreateVersionOneFixture("failed-step.yata");
        byte[] original = File.ReadAllBytes(sourcePath);

        Action act = () => StorageUpgradeOrchestrator.Upgrade(
            sourcePath,
            new StorageUpgradeOptions(),
            [new ThrowingUpgradeStep()],
            faultInjector: null);

        act.Should().Throw<InjectedFailureException>();
        File.ReadAllBytes(sourcePath).Should().Equal(original);
        Directory.GetFiles(_directory, "*.upgrade-target").Should().BeEmpty();
        File.Exists(StorageUpgradeOrchestrator.GetMarkerPath(sourcePath)).Should().BeFalse();
    }

    private string CreateCurrentDatabase(string fileName)
    {
        string path = Path.Combine(_directory, fileName);
        using (var database = YatagarasuDatabase.Open(path))
        {
            using var transaction = database.BeginWriteTransaction();
            transaction.CreateVertex("Seed");
            transaction.Commit();
        }
        return path;
    }

    private string CreateVersionOneFixture(string fileName)
    {
        string path = CreateCurrentDatabase(fileName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = 9;
        stream.WriteByte(1);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static void AssertSourceOrTargetIsAuthoritative(string sourcePath, byte[] original)
    {
        if (File.Exists(sourcePath))
        {
            byte version = StorageFormatInspector.Inspect(sourcePath);
            version.Should().BeOneOf((byte)1, StorageFormatVersion.Current);
            return;
        }

        string backupPath = sourcePath + ".pre-upgrade-v1.bak";
        File.Exists(backupPath).Should().BeTrue();
        File.ReadAllBytes(backupPath).Should().Equal(original);
    }

    private sealed class TestUpgradeStep : IStorageUpgradeStep
    {
        public byte SourceVersion => 1;

        public byte TargetVersion => StorageFormatVersion.Current;

        public void ValidateSource(StorageUpgradeSource source)
        {
            source.Data.CanRead.Should().BeTrue();
            source.WalFilePath.Should().Be(source.FilePath + "-wal");
        }

        public void Upgrade(
            StorageUpgradeSource source,
            string targetFilePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var database = YatagarasuDatabase.Open(targetFilePath);
            using var transaction = database.BeginWriteTransaction();
            transaction.CreateVertex("Upgraded");
            transaction.Commit();
        }

        public void ValidateTarget(string targetFilePath)
        {
            using var database = YatagarasuDatabase.Open(targetFilePath);
            database.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
        }
    }

    private sealed class ThrowAtBoundary(StorageUpgradeBoundary boundary)
        : IStorageUpgradeFaultInjector
    {
        public void OnBoundary(StorageUpgradeBoundary current)
        {
            if (current == boundary)
                throw new InjectedFailureException();
        }
    }

    private sealed class ThrowingUpgradeStep : IStorageUpgradeStep
    {
        public byte SourceVersion => 1;

        public byte TargetVersion => StorageFormatVersion.Current;

        public void ValidateSource(StorageUpgradeSource source)
        {
        }

        public void Upgrade(
            StorageUpgradeSource source,
            string targetFilePath,
            CancellationToken cancellationToken)
        {
            File.WriteAllBytes(targetFilePath, "partial"u8.ToArray());
            throw new InjectedFailureException();
        }

        public void ValidateTarget(string targetFilePath)
        {
        }
    }

    private sealed class InjectedFailureException : Exception;
}
