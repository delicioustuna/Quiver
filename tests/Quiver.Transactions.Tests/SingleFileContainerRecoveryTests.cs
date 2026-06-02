using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// ARCH-4 増分2b: 単一ファイルコンテナ上で WAL ロギング + crash recovery が成立することを検証する。
/// option B — 全テナントを 1 つの dataFileKind で WAL に載せ、recovery は物理ページ単位で動く
/// (カタログ / page-table も物理ページなので committed tx のページイメージとして透過的に redo される)。
///
/// crash sim: ストア init (= ヘッダ/カタログ/page-table の確立) はトランザクション外で行われ
/// データファイルへ直接永続化される。そこで「init をフラッシュした時点のデータファイル」を
/// <c>crashPath</c> にスナップショットし、その後の committed/aborted トランザクションを WAL に
/// 記録してから、<b>WAL を crashPath へ recover</b> する。これは「チェックポイント済みデータ +
/// 未フラッシュの committed tx は WAL にのみ」というクラッシュ状況の正確な再現。物理ページ ID は
/// スナップショットで揃うため redo 先が一致する。
/// </summary>
public class SingleFileContainerRecoveryTests : IDisposable
{
    private const byte DataFileKind = 1;
    // カタログ内のテナント ID (WAL fileKind とは別空間)。
    private const byte NodesTenant = (byte)WalFileKind.Nodes;
    private const byte NodeVerTenant = (byte)WalFileKind.NodeVersionMeta;

    private readonly string _walDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _srcPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
    private readonly string _crashPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
    private readonly WriteAheadLog _wal;

    public SingleFileContainerRecoveryTests()
    {
        _wal = new WriteAheadLog(_walDir);
    }

    public void Dispose()
    {
        _wal.Dispose();
        if (Directory.Exists(_walDir)) Directory.Delete(_walDir, recursive: true);
        if (File.Exists(_srcPath)) File.Delete(_srcPath);
        if (File.Exists(_crashPath)) File.Delete(_crashPath);
    }

    private static NodeStore OpenNodeStore(SingleFileContainer c)
    {
        var nodeT = c.OpenTenant(NodesTenant, PageKind.Header);
        var verT = c.OpenTenant(NodeVerTenant, PageKind.Header);
        var versions = new EntityVersionStore(verT);
        return new NodeStore(nodeT, labelIndex: null, versions);
    }

    /// <summary>ストアを init してデータファイルへフラッシュし、その時点を crashPath へスナップショットする。</summary>
    private void InitAndSnapshot()
    {
        using (var src = new SingleFileContainer(_srcPath))
        {
            src.EnableWalLogging(DataFileKind, _wal);
            OpenNodeStore(src); // ctor がヘッダ/カタログ/page-table を init (tx 外 = WAL 非対象)
            src.Flush();
        }
        File.Copy(_srcPath, _crashPath, overwrite: true);
    }

    [Fact]
    public void Committed_node_is_recovered()
    {
        InitAndSnapshot();

        NodeId id;
        // 再オープンして committed tx を 1 件流す。tx 中のページ変更だけが WAL に乗る。
        using (var src = new SingleFileContainer(_srcPath))
        {
            src.EnableWalLogging(DataFileKind, _wal);
            var store = OpenNodeStore(src);

            var txId = new TransactionId(42);
            _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
            WalPageContext.Begin(_wal, txId);
            id = store.Allocate(new LabelId(7));
            WalPageContext.FlushPending();
            long commitLsn = _wal.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
            _wal.FlushTo(commitLsn);
            WalPageContext.End();
        }

        // pre-tx スナップショット (crashPath) へ WAL を recover。
        using var dst = new SingleFileContainer(_crashPath);
        var registry = new Dictionary<byte, IPagedFile> { { DataFileKind, dst.Physical } };
        new RecoveryManager(new NullPageManager(), _wal, registry).Recover();
        dst.ReloadCatalog();

        var recovered = OpenNodeStore(dst);
        using var h = recovered.Read(id);
        h.InUse.Should().BeTrue();
        h.Label.Value.Should().Be(7);
    }

    [Fact]
    public void Aborted_node_is_not_recovered()
    {
        InitAndSnapshot();

        NodeId id;
        using (var src = new SingleFileContainer(_srcPath))
        {
            src.EnableWalLogging(DataFileKind, _wal);
            var store = OpenNodeStore(src);

            var txId = new TransactionId(99);
            _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
            WalPageContext.Begin(_wal, txId);
            id = store.Allocate(new LabelId(7));
            WalPageContext.FlushPending();
            // Commit ではなく Abort。recovery は committed でないページイメージを redo しない。
            long abortLsn = _wal.Append(WalRecordType.Abort, txId, ReadOnlySpan<byte>.Empty);
            _wal.FlushTo(abortLsn);
            WalPageContext.End();
        }

        using var dst = new SingleFileContainer(_crashPath);
        var registry = new Dictionary<byte, IPagedFile> { { DataFileKind, dst.Physical } };
        new RecoveryManager(new NullPageManager(), _wal, registry).Recover();
        dst.ReloadCatalog();

        // 中断 tx のレコード / ヘッダ更新は redo されないため、ノードは存在しない (hwm は pre-tx のまま)。
        var recovered = OpenNodeStore(dst);
        using var h = recovered.Read(id);
        h.InUse.Should().BeFalse();
    }

    // RecoveryManager は IPageManager を受け取るが Recover() では使わないため no-op で足りる。
    private sealed class NullPageManager : IPageManager
    {
        public IPagedFile OpenOrCreate(string path, PageKind defaultKind)
            => throw new NotSupportedException();
        public void FlushAll() { }
        public void Dispose() { }
    }
}
