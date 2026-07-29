using System.Text.Json;
using Quiver.Core;

namespace Quiver.Storage.Upgrade;

internal interface IStorageUpgradeStep
{
    byte SourceVersion { get; }

    byte TargetVersion { get; }

    void ValidateSource(StorageUpgradeSource source);

    void Upgrade(StorageUpgradeSource source, string targetFilePath, CancellationToken cancellationToken);

    void ValidateTarget(string targetFilePath);
}

internal sealed class StorageUpgradeSource(string filePath, Stream data)
{
    internal string FilePath { get; } = filePath;

    internal Stream Data { get; } = data;

    internal string WalFilePath => FilePath + "-wal";
}

internal interface IStorageUpgradeFaultInjector
{
    void OnBoundary(StorageUpgradeBoundary boundary);
}

internal enum StorageUpgradeBoundary
{
    TargetValidated,
    MarkerPersisted,
    SourceMoved,
    TargetMoved,
}

internal static class StorageUpgradeOrchestrator
{
    private const string MarkerSuffix = ".quiver-upgrade";
    private static readonly IReadOnlyList<IStorageUpgradeStep> NoSteps = [];

    internal static StorageUpgradeResult Upgrade(
        string filePath,
        StorageUpgradeOptions? options = null)
        => Upgrade(filePath, options, NoSteps, faultInjector: null);

    internal static StorageUpgradeResult Upgrade(
        string filePath,
        StorageUpgradeOptions? options,
        IReadOnlyList<IStorageUpgradeStep> steps,
        IStorageUpgradeFaultInjector? faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(steps);
        options ??= new StorageUpgradeOptions();
        options.CancellationToken.ThrowIfCancellationRequested();

        string sourcePath = Path.GetFullPath(filePath);
        string markerPath = GetMarkerPath(sourcePath);
        if (File.Exists(markerPath))
            return ResumeSwitch(markerPath);

        using FileStream sourceStream = StorageFormatInspector.OpenExclusiveRead(sourcePath);
        byte sourceVersion = StorageFormatInspector.Inspect(sourceStream);
        if (sourceVersion == StorageFormatVersion.Current)
        {
            return new StorageUpgradeResult(
                StorageUpgradeStatus.AlreadyCurrent,
                sourceVersion,
                StorageFormatVersion.Current,
                BackupFilePath: null);
        }

        IReadOnlyList<IStorageUpgradeStep> path = ResolvePath(
            sourceVersion,
            StorageFormatVersion.Current,
            steps);
        string directory = Path.GetDirectoryName(sourcePath)!;
        string backupPath = ResolveBackupPath(sourcePath, sourceVersion, options);
        string? workingPath = null;
        Stream? workingStream = null;
        var createdTargets = new List<string>();
        bool markerPersisted = false;

        try
        {
            string currentPath = sourcePath;
            foreach (IStorageUpgradeStep step in path)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var source = new StorageUpgradeSource(
                    currentPath,
                    workingStream ?? sourceStream);
                step.ValidateSource(source);

                string nextPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(sourcePath)}.{Guid.NewGuid():N}.upgrade-target");
                createdTargets.Add(nextPath);
                step.Upgrade(source, nextPath, options.CancellationToken);
                FlushFileToDisk(nextPath);

                byte actualTargetVersion = StorageFormatInspector.Inspect(nextPath);
                if (actualTargetVersion != step.TargetVersion)
                {
                    throw new StorageException(
                        $"Storage upgrade step produced family version {actualTargetVersion}; " +
                        $"version {step.TargetVersion} was required.");
                }
                step.ValidateTarget(nextPath);

                workingStream?.Dispose();
                if (workingPath is not null)
                    File.Delete(workingPath);

                workingPath = nextPath;
                currentPath = nextPath;
                workingStream = StorageFormatInspector.OpenExclusiveRead(nextPath);
            }

            if (workingPath is null)
                throw new StorageException("Storage upgrade path did not produce a target file.");

            workingStream?.Dispose();
            workingStream = null;
            faultInjector?.OnBoundary(StorageUpgradeBoundary.TargetValidated);
            EnsureBackupDoesNotExist(backupPath);

            var marker = new StorageUpgradeMarker
            {
                SourcePath = sourcePath,
                TargetPath = workingPath,
                BackupPath = backupPath,
                SourceVersion = sourceVersion,
                TargetVersion = StorageFormatVersion.Current,
                KeepBackup = options.KeepBackup,
            };
            WriteMarker(markerPath, marker);
            markerPersisted = true;
            faultInjector?.OnBoundary(StorageUpgradeBoundary.MarkerPersisted);

            sourceStream.Dispose();
            return CompleteSwitch(markerPath, marker, faultInjector);
        }
        finally
        {
            workingStream?.Dispose();
            if (!markerPersisted)
            {
                foreach (string targetPath in createdTargets)
                    TryDelete(targetPath);
            }
        }
    }

    internal static string GetMarkerPath(string sourcePath)
        => Path.GetFullPath(sourcePath) + MarkerSuffix;

    private static IReadOnlyList<IStorageUpgradeStep> ResolvePath(
        byte sourceVersion,
        byte targetVersion,
        IReadOnlyList<IStorageUpgradeStep> steps)
    {
        var bySource = new Dictionary<byte, IStorageUpgradeStep>();
        foreach (IStorageUpgradeStep step in steps)
        {
            if (!bySource.TryAdd(step.SourceVersion, step))
                throw new InvalidOperationException(
                    $"Multiple storage upgrade steps start at family version {step.SourceVersion}.");
        }

        var result = new List<IStorageUpgradeStep>();
        var visited = new HashSet<byte>();
        byte current = sourceVersion;
        while (current != targetVersion)
        {
            if (!visited.Add(current) || !bySource.TryGetValue(current, out IStorageUpgradeStep? step))
                throw new StorageUpgradeNotSupportedException(sourceVersion, targetVersion);
            result.Add(step);
            current = step.TargetVersion;
        }
        return result;
    }

    private static string ResolveBackupPath(
        string sourcePath,
        byte sourceVersion,
        StorageUpgradeOptions options)
    {
        string sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        string backupPath = options.BackupFilePath is null
            ? sourcePath + $".pre-upgrade-v{sourceVersion}.bak"
            : Path.GetFullPath(options.BackupFilePath);

        if (!string.Equals(
                sourceDirectory,
                Path.GetDirectoryName(backupPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "BackupFilePath must be in the same directory as the source database.",
                nameof(options));
        }
        if (string.Equals(sourcePath, backupPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("BackupFilePath must differ from the source database path.", nameof(options));

        return options.KeepBackup
            ? backupPath
            : Path.Combine(
                sourceDirectory,
                $".{Path.GetFileName(sourcePath)}.{Guid.NewGuid():N}.upgrade-rollback");
    }

    private static void EnsureBackupDoesNotExist(string backupPath)
    {
        if (File.Exists(backupPath))
            throw new IOException($"Storage upgrade backup already exists: '{backupPath}'.");
    }

    private static StorageUpgradeResult ResumeSwitch(string markerPath)
    {
        StorageUpgradeMarker marker = ReadMarker(markerPath);
        return CompleteSwitch(markerPath, marker, faultInjector: null);
    }

    private static StorageUpgradeResult CompleteSwitch(
        string markerPath,
        StorageUpgradeMarker marker,
        IStorageUpgradeFaultInjector? faultInjector)
    {
        bool sourceExists = File.Exists(marker.SourcePath);
        bool targetExists = File.Exists(marker.TargetPath);
        bool backupExists = File.Exists(marker.BackupPath);

        if (sourceExists)
        {
            byte sourceVersion;
            try
            {
                sourceVersion = StorageFormatInspector.Inspect(marker.SourcePath);
            }
            catch (Exception exception) when (IsStorageInspectionFailure(exception) && backupExists)
            {
                RestoreBackup(markerPath, marker);
                throw new StorageException(
                    "The installed storage-upgrade target was invalid; the original database was restored.",
                    exception);
            }
            if (sourceVersion == marker.SourceVersion)
            {
                if (!targetExists)
                {
                    File.Delete(markerPath);
                    throw new StorageException(
                        "The storage-upgrade target was unavailable; the original database was left unchanged.");
                }
                if (backupExists)
                    throw InvalidMarkerState(markerPath);

                EnsureTargetIsValidOrRestore(markerPath, marker, sourceWasMoved: false);

                File.Move(marker.SourcePath, marker.BackupPath);
                faultInjector?.OnBoundary(StorageUpgradeBoundary.SourceMoved);
                sourceExists = false;
                backupExists = true;
            }
            else if (sourceVersion != marker.TargetVersion)
            {
                throw InvalidMarkerState(markerPath);
            }
            else if (!backupExists)
            {
                throw InvalidMarkerState(markerPath);
            }
        }

        if (!sourceExists)
        {
            if (!backupExists)
                throw InvalidMarkerState(markerPath);
            if (!targetExists)
            {
                File.Move(marker.BackupPath, marker.SourcePath);
                File.Delete(markerPath);
                throw new StorageException(
                    "Storage upgrade target was unavailable; the original database was restored.");
            }

            EnsureTargetIsValidOrRestore(markerPath, marker, sourceWasMoved: true);
            File.Move(marker.TargetPath, marker.SourcePath);
            faultInjector?.OnBoundary(StorageUpgradeBoundary.TargetMoved);
        }

        byte installedVersion = StorageFormatInspector.Inspect(marker.SourcePath);
        if (installedVersion != marker.TargetVersion)
            throw InvalidMarkerState(markerPath);

        if (!marker.KeepBackup)
            DeleteIfExists(marker.BackupPath);
        DeleteIfExists(marker.TargetPath);
        File.Delete(markerPath);

        return new StorageUpgradeResult(
            StorageUpgradeStatus.Upgraded,
            marker.SourceVersion,
            marker.TargetVersion,
            marker.KeepBackup ? marker.BackupPath : null);
    }

    private static void EnsureTargetIsValidOrRestore(
        string markerPath,
        StorageUpgradeMarker marker,
        bool sourceWasMoved)
    {
        try
        {
            byte targetVersion = StorageFormatInspector.Inspect(marker.TargetPath);
            if (targetVersion != marker.TargetVersion)
            {
                throw new StorageException(
                    $"Storage upgrade target has family version {targetVersion}; " +
                    $"version {marker.TargetVersion} was required.");
            }
        }
        catch (Exception exception) when (IsStorageInspectionFailure(exception))
        {
            if (sourceWasMoved)
                RestoreBackup(markerPath, marker);
            else
            {
                TryDelete(marker.TargetPath);
                File.Delete(markerPath);
            }

            string disposition = sourceWasMoved ? "restored" : "left unchanged";
            throw new StorageException(
                $"The storage-upgrade target was invalid; the original database was {disposition}.",
                exception);
        }
    }

    private static void RestoreBackup(
        string markerPath,
        StorageUpgradeMarker marker)
    {
        if (File.Exists(marker.SourcePath))
        {
            TryDelete(marker.TargetPath);
            File.Move(marker.SourcePath, marker.TargetPath);
        }
        else
        {
            TryDelete(marker.TargetPath);
        }

        File.Move(marker.BackupPath, marker.SourcePath);
        File.Delete(markerPath);
    }

    private static bool IsStorageInspectionFailure(Exception exception)
        => exception is QuiverException or IOException or UnauthorizedAccessException;

    private static CorruptionException InvalidMarkerState(string markerPath)
        => new($"Storage upgrade marker has an inconsistent switch state: '{markerPath}'.");

    private static void FlushFileToDisk(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteMarker(string markerPath, StorageUpgradeMarker marker)
    {
        string temporaryMarkerPath = markerPath + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(marker);
        try
        {
            DeleteIfExists(temporaryMarkerPath);
            using (var stream = new FileStream(
                       temporaryMarkerPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryMarkerPath, markerPath);
        }
        finally
        {
            TryDelete(temporaryMarkerPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static StorageUpgradeMarker ReadMarker(string markerPath)
    {
        try
        {
            byte[] payload = File.ReadAllBytes(markerPath);
            StorageUpgradeMarker? marker = JsonSerializer.Deserialize<StorageUpgradeMarker>(payload);
            if (marker is null
                || string.IsNullOrWhiteSpace(marker.SourcePath)
                || string.IsNullOrWhiteSpace(marker.TargetPath)
                || string.IsNullOrWhiteSpace(marker.BackupPath))
            {
                throw InvalidMarkerState(markerPath);
            }
            ValidateMarkerPaths(markerPath, marker);
            return marker;
        }
        catch (JsonException exception)
        {
            throw new CorruptionException(
                $"Storage upgrade marker is malformed: '{markerPath}'.",
                exception);
        }
    }

    private static void ValidateMarkerPaths(
        string markerPath,
        StorageUpgradeMarker marker)
    {
        string expectedSource = markerPath[..^MarkerSuffix.Length];
        string source = Path.GetFullPath(marker.SourcePath);
        string target = Path.GetFullPath(marker.TargetPath);
        string backup = Path.GetFullPath(marker.BackupPath);
        string directory = Path.GetDirectoryName(expectedSource)!;

        if (!string.Equals(source, expectedSource, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(target), directory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(backup), directory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, backup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, backup, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidMarkerState(markerPath);
        }

        marker.SourcePath = source;
        marker.TargetPath = target;
        marker.BackupPath = backup;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StorageUpgradeMarker
    {
        public string SourcePath { get; set; } = string.Empty;

        public string TargetPath { get; set; } = string.Empty;

        public string BackupPath { get; set; } = string.Empty;

        public byte SourceVersion { get; set; }

        public byte TargetVersion { get; set; }

        public bool KeepBackup { get; set; }
    }
}
