using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Index.FullText;

internal readonly record struct FullTextSegmentArtifactRef(
    Guid ArtifactId,
    int Length,
    uint Checksum);

internal sealed record FullTextDurableManifest(
    long Generation,
    long Xmin,
    long? Xmax,
    long SourceCommittedHighWater,
    bool CoversPrimarySnapshot,
    ImmutableArray<FullTextSegmentArtifactRef> Segments);

/// <summary>
/// immutable全文segment bodyを保持するappend-only artifact store。
/// bodyをchecksum付きでfsyncしてから、呼び出し側がpage-WAL内のmanifestをcommitする。
/// </summary>
internal sealed class FullTextSegmentArtifactStore : IDisposable
{
    private const uint RecordMagic = 0x4753_5446; // "FTSG"
    private const int RecordVersion = 1;
    private const int RecordHeaderSize = 16;
    private const string ArtifactExtension = ".qfts";
    private const uint ManifestMagic = 0x4D46_5446; // "FTFM"
    private const int ManifestVersion = 2;
    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly Dictionary<Guid, byte[]> _memoryArtifacts = [];
    private bool _disposed;

    internal FullTextSegmentArtifactStore(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        _path = path;
        Directory.CreateDirectory(path);
    }

    internal FullTextSegmentArtifactRef Append(ImmutableFullTextSegment segment)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] body = SerializeSegment(segment);
        uint checksum = Crc32.HashToUInt32(body);
        byte[] record = new byte[RecordHeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, RecordMagic);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), RecordVersion);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), checksum);
        body.CopyTo(record.AsSpan(RecordHeaderSize));
        lock (_gate)
        {
            Guid artifactId;
            do artifactId = Guid.NewGuid();
            while (_memoryArtifacts.ContainsKey(artifactId)
                   || _path is not null && File.Exists(GetArtifactPath(artifactId)));

            if (_path is null)
            {
                _memoryArtifacts.Add(artifactId, record);
            }
            else
            {
                using var file = new FileStream(
                    GetArtifactPath(artifactId),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.WriteThrough);
                file.Write(record);
                file.Flush(flushToDisk: true);
            }
            return new(artifactId, body.Length, checksum);
        }
    }

    internal ImmutableFullTextSegment Read(FullTextSegmentArtifactRef reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            byte[] record;
            if (_path is null)
            {
                if (!_memoryArtifacts.TryGetValue(reference.ArtifactId, out record!))
                    throw new CorruptionException(
                        $"全文segment artifactがありません: id={reference.ArtifactId:N}.");
            }
            else
            {
                string artifactPath = GetArtifactPath(reference.ArtifactId);
                if (!File.Exists(artifactPath))
                    throw new CorruptionException(
                        $"全文segment artifactがありません: id={reference.ArtifactId:N}.");
                record = File.ReadAllBytes(artifactPath);
            }

            if (reference.Length < 0
                || record.Length != RecordHeaderSize + reference.Length)
                throw new CorruptionException(
                    $"全文segment artifact参照の長さが一致しません: id={reference.ArtifactId:N}, length={reference.Length}.");

            ReadOnlySpan<byte> header = record.AsSpan(0, RecordHeaderSize);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            if (magic != RecordMagic
                || version != RecordVersion
                || length != reference.Length
                || checksum != reference.Checksum)
                throw new CorruptionException(
                    $"全文segment artifact headerがmanifest参照と一致しません: id={reference.ArtifactId:N}.");

            byte[] body = record.AsSpan(RecordHeaderSize).ToArray();
            if (Crc32.HashToUInt32(body) != checksum)
                throw new CorruptionException(
                    $"全文segment artifactのchecksumが一致しません: id={reference.ArtifactId:N}.");
            return DeserializeSegment(body);
        }
    }

    internal int CollectGarbage(IReadOnlySet<Guid> retainedArtifactIds)
    {
        ArgumentNullException.ThrowIfNull(retainedArtifactIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            int reclaimed = 0;
            if (_path is null)
            {
                foreach (Guid artifactId in _memoryArtifacts.Keys
                             .Where(id => !retainedArtifactIds.Contains(id))
                             .ToArray())
                {
                    _memoryArtifacts.Remove(artifactId);
                    reclaimed++;
                }
                return reclaimed;
            }

            foreach (string artifactPath in Directory.EnumerateFiles(
                         _path,
                         "*" + ArtifactExtension,
                         SearchOption.TopDirectoryOnly))
            {
                if (Guid.TryParseExact(
                        Path.GetFileNameWithoutExtension(artifactPath),
                        "N",
                        out Guid artifactId)
                    && retainedArtifactIds.Contains(artifactId))
                    continue;
                File.Delete(artifactPath);
                reclaimed++;
            }
            return reclaimed;
        }
    }

    internal int CountGarbage(IReadOnlySet<Guid> retainedArtifactIds)
    {
        ArgumentNullException.ThrowIfNull(retainedArtifactIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_path is null)
                return _memoryArtifacts.Keys.Count(id => !retainedArtifactIds.Contains(id));
            return Directory.EnumerateFiles(
                    _path,
                    "*" + ArtifactExtension,
                    SearchOption.TopDirectoryOnly)
                .Count(artifactPath =>
                    !Guid.TryParseExact(
                        Path.GetFileNameWithoutExtension(artifactPath),
                        "N",
                        out Guid artifactId)
                    || !retainedArtifactIds.Contains(artifactId));
        }
    }

    internal static string EncodeManifest(FullTextDurableManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ManifestMagic);
            writer.Write(ManifestVersion);
            writer.Write(manifest.Generation);
            writer.Write(manifest.Xmin);
            writer.Write(manifest.Xmax.HasValue);
            if (manifest.Xmax is { } xmax)
                writer.Write(xmax);
            writer.Write(manifest.SourceCommittedHighWater);
            writer.Write(manifest.CoversPrimarySnapshot);
            writer.Write(manifest.Segments.Length);
            foreach (FullTextSegmentArtifactRef segment in manifest.Segments)
            {
                writer.Write(segment.ArtifactId.ToByteArray());
                writer.Write(segment.Length);
                writer.Write(segment.Checksum);
            }
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    internal static FullTextDurableManifest DecodeManifest(string encoded)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != ManifestMagic
                || reader.ReadInt32() != ManifestVersion)
                throw new CorruptionException("全文segment manifestの形式が不正です。");
            long generation = reader.ReadInt64();
            long xmin = reader.ReadInt64();
            long? xmax = reader.ReadBoolean() ? reader.ReadInt64() : null;
            long sourceHighWater = reader.ReadInt64();
            bool coversPrimary = reader.ReadBoolean();
            int count = reader.ReadInt32();
            if (count < 0 || count > 1024)
                throw new CorruptionException(
                    $"全文segment manifestのsegment数が不正です: {count}.");
            var segments = ImmutableArray.CreateBuilder<FullTextSegmentArtifactRef>(count);
            for (int i = 0; i < count; i++)
                segments.Add(new(
                    new Guid(reader.ReadBytes(16)),
                    reader.ReadInt32(),
                    reader.ReadUInt32()));
            if (stream.Position != stream.Length)
                throw new CorruptionException("全文segment manifestに未知の末尾データがあります。");
            return new(
                generation,
                xmin,
                xmax,
                sourceHighWater,
                coversPrimary,
                segments.MoveToImmutable());
        }
        catch (CorruptionException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is FormatException
                or EndOfStreamException
                or IOException
                or ArgumentException)
        {
            throw new CorruptionException("全文segment manifestを読み取れません。", ex);
        }
    }

    private static byte[] SerializeSegment(ImmutableFullTextSegment segment)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(segment.Entries.Count);
            foreach (FullTextStoredEntry entry in segment.Entries)
            {
                writer.Write(entry.Owner);
                writer.Write(entry.PropertyVersion.Value);
                writer.Write(entry.IsTombstone);
            }

            writer.Write(segment.Norms.Count);
            foreach ((long owner, int norm) in segment.Norms.OrderBy(static x => x.Key))
            {
                writer.Write(owner);
                writer.Write(norm);
            }

            writer.Write(segment.Postings.Count);
            foreach ((string term, List<(long Owner, int Tf)> postings) in
                     segment.Postings.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                writer.Write(term);
                writer.Write(postings.Count);
                foreach ((long owner, int tf) in postings)
                {
                    writer.Write(owner);
                    writer.Write(tf);
                }
            }
        }
        return stream.ToArray();
    }

    private static ImmutableFullTextSegment DeserializeSegment(byte[] body)
    {
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadInt32() != 1)
                throw new CorruptionException("全文segment bodyのversionが不正です。");

            int entryCount = ReadCount(reader, "entry");
            var entries = new FullTextStoredEntry[entryCount];
            for (int i = 0; i < entryCount; i++)
                entries[i] = new(
                    reader.ReadInt64(),
                    new PropertyVersionRef(reader.ReadInt64()),
                    reader.ReadBoolean());

            int normCount = ReadCount(reader, "norm");
            var norms = new Dictionary<long, int>(normCount);
            for (int i = 0; i < normCount; i++)
                norms.Add(reader.ReadInt64(), reader.ReadInt32());

            int termCount = ReadCount(reader, "term");
            var postings = new Dictionary<string, List<(long Owner, int Tf)>>(
                termCount,
                StringComparer.Ordinal);
            for (int i = 0; i < termCount; i++)
            {
                string term = reader.ReadString();
                int postingCount = ReadCount(reader, "posting");
                var values = new List<(long Owner, int Tf)>(postingCount);
                for (int j = 0; j < postingCount; j++)
                    values.Add((reader.ReadInt64(), reader.ReadInt32()));
                postings.Add(term, values);
            }
            if (stream.Position != stream.Length)
                throw new CorruptionException("全文segment bodyに未知の末尾データがあります。");
            return new ImmutableFullTextSegment(entries, postings, norms);
        }
        catch (CorruptionException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is EndOfStreamException
                or IOException
                or ArgumentException)
        {
            throw new CorruptionException("全文segment bodyを読み取れません。", ex);
        }
    }

    private static int ReadCount(BinaryReader reader, string kind)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
            throw new CorruptionException($"全文segment bodyの{kind}数が不正です: {count}.");
        return count;
    }

    private string GetArtifactPath(Guid artifactId)
        => Path.Combine(_path!, artifactId.ToString("N") + ArtifactExtension);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _memoryArtifacts.Clear();
        }
    }
}
