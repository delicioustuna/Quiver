// Quiver.Samples.Hosting (ASP.NET Core minimal API) を WebApplicationFactory<Program>
// 経由で in-process に立ち上げ、HTTP API の網羅的なラフネス (異常系 / 境界系 / 永続化) 検査を
// 自動化する。
//
// 主目的:
//   1. defensive read API: GET /vertices/{id} が不正・未知の不透明 ID で 404 を返す。
//   2. POST/GET の round-trip 整合性 (label / property)。
//   3. 不正リクエスト (空 body / 必須フィールド欠落) の 400 マッピング。
//   4. Edge作成 + 不在端点での 404。
//   5. 削除 → GET 再リクエストで 404 (論理削除 + visibility)。
//   6. stats endpoint の整合性 (作成数 = VertexCount)。

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Quiver.Hosting.Tests;

/// <summary>
/// Sample Hosting プロジェクトの全 endpoint を WebApplicationFactory で起動して
/// HTTP 経由で叩く統合テスト。テストごとに固有の temp ディレクトリを使い、テスト間で
/// データベース状態を共有しない。
/// </summary>
public sealed class QuiverHostingApiTests : IDisposable
{
    private readonly string _dataDir;
    private readonly QuiverWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public QuiverHostingApiTests()
    {
        _dataDir = Path.Combine(
            Path.GetTempPath(),
            "quiver_hosting_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new QuiverWebApplicationFactory(_dataDir);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, recursive: true); }
            catch { /* WAL ハンドルが遅延解放されるケースは握りつぶす */ }
        }
    }

    // ===== 1. 防御的読み取り API =====

    [Fact]
    public async Task GET_nonexistent_vertex_returns_404_without_exception()
    {
        var resp = await _client.GetAsync("/vertices/999999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            ": HWM 超 ID は Core 側で safe-return され 404 になる");
    }

    [Fact]
    public async Task GET_vertex_with_extremely_large_id_returns_404()
    {
        var resp = await _client.GetAsync($"/vertices/{long.MaxValue / 2}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_vertex_with_negative_id_returns_404()
    {
        // long.MinValue / 1 桁負値は routing で long.TryParse には通るので Core 側の bounds check に届く。
        var resp = await _client.GetAsync("/vertices/-1");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_vertex_with_non_numeric_id_returns_404_from_routing()
    {
        // {id:long} の制約により routing で reject される。
        var resp = await _client.GetAsync("/vertices/abc");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ===== 2. POST → GET round-trip =====

    [Fact]
    public async Task POST_then_GET_returns_created_vertex()
    {
        var post = await _client.PostAsJsonAsync("/vertices", new { label = "Person", name = "alice" });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await post.Content.ReadFromJsonAsync<CreatedVertexDto>();
        created.Should().NotBeNull();
        created!.Id.Should().NotBeEmpty();

        var get = await _client.GetAsync($"/vertices/{created.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadFromJsonAsync<GetVertexDto>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(created.Id);
        body.Name.Should().Be("alice");
    }

    [Fact]
    public async Task POST_vertex_without_name_succeeds_and_GET_returns_null_name()
    {
        var post = await _client.PostAsJsonAsync("/vertices", new { label = "Item" });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await post.Content.ReadFromJsonAsync<CreatedVertexDto>();
        var get = await _client.GetAsync($"/vertices/{created!.Id}");
        var body = await get.Content.ReadFromJsonAsync<GetVertexDto>();
        body!.Name.Should().BeNull();
    }

    // ===== 3. 不正リクエストのハンドリング =====

    [Fact]
    public async Task POST_vertex_with_empty_body_returns_400_or_unsupported()
    {
        // ASP.NET Core の minimal API は本文が null だと req が null として handler に届く →
        // 自前バリデーションで 400 を返す。
        using var content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/vertices", content);
        // 415 / 400 のいずれもユーザに対し「コンテンツが正しくない」を伝えるので許容。
        resp.StatusCode.Should()
            .BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task POST_vertex_with_missing_label_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("/vertices", new { name = "no-label" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_vertex_with_malformed_json_returns_400()
    {
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/vertices", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ===== 4. Edge =====

    [Fact]
    public async Task POST_edge_between_existing_vertices_succeeds()
    {
        var a = await CreateVertex("A");
        var b = await CreateVertex("B");
        var resp = await _client.PostAsJsonAsync("/edges",
            new { source = a, target = b, type = "KNOWS" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task POST_edge_with_nonexistent_source_returns_404()
    {
        var b = await CreateVertex("B");
        var resp = await _client.PostAsJsonAsync("/edges",
            new { source = Guid.NewGuid(), target = b, type = "KNOWS" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "存在しないVertexを端点にした edge 作成は 404");
    }

    [Fact]
    public async Task POST_edge_with_missing_type_returns_400()
    {
        var a = await CreateVertex("A");
        var b = await CreateVertex("B");
        var resp = await _client.PostAsJsonAsync("/edges",
            new { source = a, target = b });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ===== 5. 削除 → GET で 404 =====

    [Fact]
    public async Task DELETE_vertex_then_GET_returns_404()
    {
        var id = await CreateVertex("X");
        var del = await _client.DeleteAsync($"/vertices/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var get = await _client.GetAsync($"/vertices/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DELETE_nonexistent_vertex_returns_404()
    {
        var del = await _client.DeleteAsync("/vertices/123456789");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ===== 6. プロパティ動的 set =====

    [Fact]
    public async Task POST_property_to_nonexistent_vertex_returns_404()
    {
        var resp = await _client.PostAsJsonAsync("/vertices/777777/properties",
            new { key = "k", value = "v" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_property_then_GET_vertex_returns_value_when_key_is_name()
    {
        var id = await CreateVertex("Person");
        var setResp = await _client.PostAsJsonAsync($"/vertices/{id}/properties",
            new { key = "name", value = "bob" });
        setResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var get = await _client.GetAsync($"/vertices/{id}");
        var body = await get.Content.ReadFromJsonAsync<GetVertexDto>();
        body!.Name.Should().Be("bob");
    }

    // ===== 7. 統計 endpoint =====

    [Fact]
    public async Task GET_stats_reflects_created_vertex_count()
    {
        var baseStats = await _client.GetFromJsonAsync<StatsDto>("/stats");
        long baseline = baseStats!.VertexCount;

        for (int i = 0; i < 5; i++)
            await CreateVertex("Counted");

        var after = await _client.GetFromJsonAsync<StatsDto>("/stats");
        after!.VertexCount.Should().Be(baseline + 5);
    }

    // ===== 8. 並行 GET (例外なし) =====

    [Fact]
    public async Task Concurrent_GETs_for_mix_of_existing_and_ghost_IDs_never_throw_500()
    {
        var realIds = new List<Guid>();
        for (int i = 0; i < 8; i++)
            realIds.Add(await CreateVertex("Concurrent"));

        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 32; i++)
        {
            Guid id = (i % 2 == 0) ? realIds[i % realIds.Count] : Guid.NewGuid();
            tasks.Add(_client.GetAsync($"/vertices/{id}"));
        }
        var responses = await Task.WhenAll(tasks);
        foreach (var r in responses)
        {
            ((int)r.StatusCode).Should().BeLessThan(500,
                "concurrent な ghost ID GET でも 500 が返らない ( 中核契約)");
            r.Dispose();
        }
    }

    // ===== 9. root endpoint =====

    [Fact]
    public async Task GET_root_returns_endpoint_listing()
    {
        var resp = await _client.GetAsync("/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain("Quiver");
        json.Should().Contain("/vertices");
    }

    // ----- ヘルパー -----

    private async Task<Guid> CreateVertex(string label, string? name = null)
    {
        var resp = await _client.PostAsJsonAsync("/vertices", new { label, name });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<CreatedVertexDto>();
        return dto!.Id;
    }

    private sealed record CreatedVertexDto(Guid Id, string Label, string? Name);
    private sealed record GetVertexDto(Guid Id, string? Name);
    private sealed record StatsDto(long VertexCount, long EdgeCount);
}

/// <summary>
/// WebApplicationFactory で sample Program を起動するときに、テスト用 temp ディレクトリと
/// 必要な appsetting を差し込む。
/// </summary>
internal sealed class QuiverWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDir;

    internal QuiverWebApplicationFactory(string dataDir) => _dataDir = dataDir;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quiver:DataDirectory"] = _dataDir,
            });
        });
        return base.CreateHost(builder);
    }
}
