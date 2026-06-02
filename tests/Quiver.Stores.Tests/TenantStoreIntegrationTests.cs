using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// ARCH-4 増分2a: 実ストア (NodeStore / RelationshipStore / EntityVersionStore sidecar) を
/// <see cref="SingleFileContainer"/> のテナント上で無改修のまま動かせることを検証する。
/// header ページ (論理 page1) / レコードページ (論理 page2+) / <c>EnsurePage</c> による論理空間
/// 拡張 / MVCC sidecar / reopen 時のメタ読み戻し + format チェックを、単一ファイル内で確認する。
/// </summary>
public class TenantStoreIntegrationTests : IDisposable
{
    private readonly string _tmpDir;

    public TenantStoreIntegrationTests()
    {
        _tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => System.IO.Directory.Delete(_tmpDir, recursive: true);

    private string DbFile() => System.IO.Path.Combine(_tmpDir, "graph.quiver");

    private static (NodeStore nodes, RelationshipStore rels) BuildStores(SingleFileContainer c)
    {
        var nodeFile = c.OpenTenant((byte)WalFileKind.Nodes, PageKind.Header);
        var nodeVerFile = c.OpenTenant((byte)WalFileKind.NodeVersionMeta, PageKind.Header);
        var nodeVersions = new EntityVersionStore(nodeVerFile);
        var nodes = new NodeStore(nodeFile, labelIndex: null, nodeVersions);

        var relFile = c.OpenTenant((byte)WalFileKind.Relationships, PageKind.Header);
        var relVerFile = c.OpenTenant((byte)WalFileKind.RelationshipVersionMeta, PageKind.Header);
        var relVersions = new EntityVersionStore(relVerFile);
        var rels = new RelationshipStore(relFile, relVersions);
        return (nodes, rels);
    }

    [Fact]
    public void Nodes_And_Rels_Coexist_In_Single_File_And_Persist()
    {
        string path = DbFile();
        NodeId a, b;
        RelationshipId r;
        using (var c = new SingleFileContainer(path))
        {
            var (nodes, rels) = BuildStores(c);
            a = nodes.Allocate(new LabelId(10));
            b = nodes.Allocate(new LabelId(20));
            r = rels.Create(nodes, a, b, new RelationshipTypeId(1));
            c.Flush();
        }

        using (var c = new SingleFileContainer(path))
        {
            var (nodes, rels) = BuildStores(c);
            using (var ha = nodes.Read(a))
            {
                ha.InUse.Should().BeTrue();
                ha.Label.Value.Should().Be(10);
            }
            using (var hb = nodes.Read(b))
            {
                hb.InUse.Should().BeTrue();
                hb.Label.Value.Should().Be(20);
            }
            using var hr = rels.Read(r);
            hr.InUse.Should().BeTrue();
            hr.Source.Should().Be(a);
            hr.Target.Should().Be(b);
            hr.Type.Value.Should().Be(1);
        }

        // 静止時は graph.quiver 単一ファイルのみ (WAL 配線は増分2b)。
        System.IO.Directory.GetFiles(_tmpDir).Should().ContainSingle()
            .Which.Should().EndWith("graph.quiver");
    }

    [Fact]
    public void Many_Nodes_Span_Multiple_Record_Pages_On_A_Tenant()
    {
        // EnsurePage による論理ページ拡張がテナント上で正しく動くことを確認する。
        using var c = new SingleFileContainer(DbFile());
        var (nodes, _) = BuildStores(c);

        int n = NodeStore.RecordsPerPage + 50; // レコードページ境界を確実に跨ぐ
        var ids = new List<NodeId>(n);
        for (int i = 0; i < n; i++)
            ids.Add(nodes.Allocate(new LabelId((short)(i % 100))));

        nodes.InUseCount.Should().Be(n);
        for (int i = 0; i < n; i++)
        {
            using var h = nodes.Read(ids[i]);
            h.InUse.Should().BeTrue();
            h.Label.Value.Should().Be((short)(i % 100));
        }
    }

    [Fact]
    public void Node_Write_FirstRel_Persists_Across_Reopen()
    {
        string path = DbFile();
        NodeId id;
        using (var c = new SingleFileContainer(path))
        {
            var (nodes, _) = BuildStores(c);
            id = nodes.Allocate(new LabelId(1));
            var w = nodes.Write(id);
            w.FirstRelationshipId = new RelationshipId(7);
            w.Dispose();
            c.Flush();
        }
        using (var c = new SingleFileContainer(path))
        {
            var (nodes, _) = BuildStores(c);
            using var h = nodes.Read(id);
            h.FirstRelationshipId.Value.Should().Be(7);
        }
    }
}
