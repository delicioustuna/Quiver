using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Yatagarasu.Storage.Wal;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

/// <summary>
/// versioned entity store と EntityVersionStore sidecar を
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

    private string DbFile() => System.IO.Path.Combine(_tmpDir, "graph.yata");

    private static (VersionedVertexStore vertices, VersionedEdgeStore edges) BuildStores(SingleFileContainer c)
    {
        var vertexFile = c.OpenTenant((byte)WalFileKind.Vertices, PageKind.Header);
        var vertexMapFile = c.OpenTenant(14, PageKind.Header);
        var vertexVerFile = c.OpenTenant((byte)WalFileKind.VertexVersionMeta, PageKind.Header);
        var vertexVersions = new EntityVersionStore(vertexVerFile);
        var vertices = new VersionedVertexStore(
            vertexFile, new ItemPointerMap(vertexMapFile), labelIndex: null, vertexVersions);

        var edgeFile = c.OpenTenant((byte)WalFileKind.Edges, PageKind.Header);
        var edgeMapFile = c.OpenTenant(15, PageKind.Header);
        var relVerFile = c.OpenTenant((byte)WalFileKind.EdgeVersionMeta, PageKind.Header);
        var edgeVersions = new EntityVersionStore(relVerFile);
        var edges = new VersionedEdgeStore(edgeFile, new ItemPointerMap(edgeMapFile), edgeVersions);
        return (vertices, edges);
    }

    [Fact]
    public void Vertices_And_Edges_Coexist_In_Single_File_And_Persist()
    {
        string path = DbFile();
        VertexId a, b;
        EdgeId r;
        using (var c = new SingleFileContainer(path))
        {
            var (vertices, edges) = BuildStores(c);
            a = vertices.Allocate(new LabelId(10));
            b = vertices.Allocate(new LabelId(20));
            r = edges.Create(vertices, a, b, new EdgeTypeId(1));
            c.Flush();
        }

        using (var c = new SingleFileContainer(path))
        {
            var (vertices, edges) = BuildStores(c);
            using (var ha = vertices.Read(a))
            {
                ha.InUse.Should().BeTrue();
                ha.Label.Value.Should().Be(10);
            }
            using (var hb = vertices.Read(b))
            {
                hb.InUse.Should().BeTrue();
                hb.Label.Value.Should().Be(20);
            }
            using var hr = edges.Read(r);
            hr.InUse.Should().BeTrue();
            hr.Id.Should().Be(r);
            hr.Source.Sequence.Should().Be(a.Sequence);
            hr.Target.Sequence.Should().Be(b.Sequence);
            hr.Type.Value.Should().Be(1);
        }

        // 静止時は graph.yata 単一ファイルのみ (WAL 配線は増分2b)。
        System.IO.Directory.GetFiles(_tmpDir).Should().ContainSingle()
            .Which.Should().EndWith("graph.yata");
    }

    [Fact]
    public void Many_Vertices_Span_Multiple_Record_Pages_On_A_Tenant()
    {
        // EnsurePage による論理ページ拡張がテナント上で正しく動くことを確認する。
        using var c = new SingleFileContainer(DbFile());
        var (vertices, _) = BuildStores(c);

        const int n = 500;
        var ids = new List<VertexId>(n);
        for (int i = 0; i < n; i++)
            ids.Add(vertices.Allocate(new LabelId((short)(i % 100))));

        vertices.InUseCount.Should().Be(n);
        for (int i = 0; i < n; i++)
        {
            using var h = vertices.Read(ids[i]);
            h.InUse.Should().BeTrue();
            h.Label.Value.Should().Be((short)(i % 100));
        }
    }

    [Fact]
    public void Vertex_Write_FirstEdge_Persists_Across_Reopen()
    {
        string path = DbFile();
        VertexId id;
        using (var c = new SingleFileContainer(path))
        {
            var (vertices, _) = BuildStores(c);
            id = vertices.Allocate(new LabelId(1));
            var w = vertices.Write(id);
            w.FirstEdgeId = new EdgeId(7);
            w.Dispose();
            c.Flush();
        }
        using (var c = new SingleFileContainer(path))
        {
            var (vertices, _) = BuildStores(c);
            using var h = vertices.Read(id);
            h.FirstEdgeId.Value.Should().Be(7);
        }
    }
}
