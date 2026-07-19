using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;

namespace Quiver.Index.FullText;

internal readonly record struct FullTextSegmentArtifactRef(
    long Offset,
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
    private const uint ManifestMagic = 0x4D46_5446; // "FTFM"
    private const int ManifestVersion = 1;
    private readonly Lock _gate = new();
    private readonly string? _path;
    private Stream? _stream;
    private bool _disposed;

    internal FullTextSegmentArtifactStore(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _stream = new MemoryStream();
            return;
        }
        _path = path;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    internal FullTextSegmentArtifactRef Append(ImmutableFullTextSegment segment)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] body = SerializeSegment(segment);
        uint checksum = Crc32.HashToUInt32(body);
        lock (_gate)
        {
            Stream stream = GetStream(create: true);
            long offset = stream.Length;
            stream.Position = offset;
            Span<byte> header = stackalloc byte[RecordHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, RecordMagic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], RecordVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checksum);
            stream.Write(header);
            stream.Write(body);
            stream.Flush();
            if (stream is FileStream file)
                file.Flush(flushToDisk: true);
            return new(offset, body.Length, checksum);
        }
    }

    internal ImmutableFullTextSegment Read(FullTextSegmentArtifactRef reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            Stream stream = GetStream(create: false);
            if (reference.Offset < 0
                || reference.Length < 0
                || reference.Offset > stream.Length - RecordHeaderSize
                || reference.Length > stream.Length - reference.Offset - RecordHeaderSize)
                throw new CorruptionException(
                    $"全文segment artifact参照がファイル範囲外です: offset={reference.Offset}, length={reference.Length}.");

            stream.Position = reference.Offset;
            Span<byte> header = stackalloc byte[RecordHeaderSize];
            stream.ReadExactly(header);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            if (magic != RecordMagic
                || version != RecordVersion
                || length != reference.Length
                || checksum != reference.Checksum)
                throw new CorruptionException(
                    $"全文segment artifact headerがmanifest参照と一致しません: offset={reference.Offset}.");

            byte[] body = new byte[length];
            stream.ReadExactly(body);
            if (Crc32.HashToUInt32(body) != checksum)
                throw new CorruptionException(
                    $"全文segment artifactのchecksumが一致しません: offset={reference.Offset}.");
            return DeserializeSegment(body);
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
                writer.Write(segment.Offset);
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
                    reader.ReadInt64(),
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

    private Stream GetStream(bool create)
    {
        if (_stream is not null)
            return _stream;
        if (_path is null)
            throw new ObjectDisposedException(nameof(FullTextSegmentArtifactStore));
        if (!create && !File.Exists(_path))
            throw new CorruptionException(
                $"全文segment artifact fileがありません: '{_path}'.");
        _stream = new FileStream(
            _path,
            create ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess | FileOptions.WriteThrough);
        return _stream;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
}
