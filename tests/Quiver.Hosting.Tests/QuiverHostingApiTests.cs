// FT-30: Quiver.Samples.Hosting (ASP.NET Core minimal API) を WebApplicationFactory<Program>
// 経由で in-process に立ち上げ、HTTP API の網羅的なラフネス (異常系 / 境界系 / 永続化) 検査を
// 自動化する。
//
// 主目的:
//   1. defensive read API (FT-30): GET /nodes/{id} が HWM 超 / 負 ID で 404 を返し例外を露出しない。
//   2. POST/GET の round-trip 整合性 (label / property)。
//   3. 不正リクエスト (空 body / 必須フィールド欠落) の 400 マッピング。
//   4. リレーションシップ作成 + 不在端点での 404。
//   5. 削除 → GET 再リクエストで 404 (論理削除 + visibility)。
//   6. stats endpoint の整合性 (作成数 = NodeCount)。

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

    // ===== 1. defensive read API (FT-30 中核) =====

    [Fact]
    public async Task GET_nonexistent_node_returns_404_without_exception()
    {
        var resp = await _client.GetAsync("/nodes/999999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "FT-30: HWM 超 ID は Core 側で safe-return され 404 になる");
    }

    [Fact]
    public async Task GET_node_with_extremely_large_id_returns_404()
    {
        var resp = await _client.GetAsync($"/nodes/{long.MaxValue / 2}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_node_with_negative_id_returns_404()
    {
        // long.MinValue / 1 桁負値は routing で long.TryParse には通るので Core 側の bounds check に届く。
        var resp = await _client.GetAsync("/nodes/-1");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_node_with_non_numeric_id_returns_404_from_routing()
    {
        // {id:long} の制約により routing で reject される。
        var resp = await _client.GetAsync("/nodes/abc");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ===== 2. POST → GET round-trip =====

    [Fact]
    public async Task POST_then_GET_returns_created_node()
    {
        var post = await _client.PostAsJsonAsync("/nodes", new { label = "Person", name = "alice" });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await post.Content.ReadFromJsonAsync<CreatedNodeDto>();
        created.Should().NotBeNull();
        created!.Id.Should().BeGreaterThanOrEqualTo(0L);

        var get = await _client.GetAsync($"/nodes/{created.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadFromJsonAsync<GetNodeDto>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(created.Id);
        body.Name.Should().Be("alice");
    }

    [Fact]
    public async Task POST_node_without_name_succeeds_and_GET_returns_null_name()
    {
        var post = await _client.PostAsJsonAsync("/nodes", new { label = "Item" });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await post.Content.ReadFromJsonAsync<CreatedNodeDto>();
        var get = await _client.GetAsync($"/nodes/{created!.Id}");
        var body = await get.Content.ReadFromJsonAsync<GetNodeDto>();
        body!.Name.Should().BeNull();
    }

    // ===== 3. 不正リクエストのハンドリング =====

    [Fact]
    public async Task POST_node_with_empty_body_returns_400_or_unsupported()
    {
        // ASP.NET Core の minimal API は本文が null だと req が null として handler に届く →
        // 自前バリデーションで 400 を返す。
        using var content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/nodes", content);
        // 415 / 400 のいずれもユーザに対し「コンテンツが正しくない」を伝えるので許容。
        resp.StatusCode.Should()
            .BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task POST_node_with_missing_label_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("/nodes", new { name = "no-label" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_node_with_malformed_json_returns_400()
    {
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/nodes", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ===== 4. リレーションシップ =====

    [Fact]
    public async Task POST_relationship_between_existing_nodes_succeeds()
    {
        var a = await CreateNode("A");
        var b = await CreateNode("B");
        var resp = await _client.PostAsJsonAsync("/relationships",
            new { source = a, target = b, type = "KNOWS" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task POST_relationship_with_nonexistent_source_returns_404()
    {
        var b = await CreateNode("B");
        var resp = await _client.PostAsJsonAsync("/relationships",
            new { source = 999_999L, target = b, type = "KNOWS" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "存在しないノードを端点にした relationship 作成は 404");
    }

    [Fact]
    public async Task POST_relationship_with_missing_type_returns_400()
    {
        var a = await CreateNode("A");
        var b = await CreateNode("B");
        var resp = await _client.PostAsJsonAsync("/relationships",
            new { source = a, target = b });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ===== 5. 削除 → GET で 404 =====

    [Fact]
    public async Task DELETE_node_then_GET_returns_404()
    {
        var id = await CreateNode("X");
        var del = await _client.DeleteAsync($"/nodes/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var get = await _client.GetAsync($"/nodes/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DELETE_nonexistent_node_returns_404()
    {
        var del = await _client.DeleteAsync("/nodes/123456789");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ===== 6. プロパティ動的 set =====

    [Fact]
    public async Task POST_property_to_nonexistent_node_returns_404()
    {
        var resp = await _client.PostAsJsonAsync("/nodes/777777/properties",
            new { key = "k", value = "v" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_property_then_GET_node_returns_value_when_key_is_name()
    {
        var id = await CreateNode("Person");
        var setResp = await _client.PostAsJsonAsync($"/nodes/{id}/properties",
            new { key = "name", value = "bob" });
        setResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var get = await _client.GetAsync($"/nodes/{id}");
        var body = await get.Content.ReadFromJsonAsync<GetNodeDto>();
        body!.Name.Should().Be("bob");
    }

    // ===== 7. stats endpoint =====

    [Fact]
    public async Task GET_stats_reflects_created_node_count()
    {
        var baseStats = await _client.GetFromJsonAsync<StatsDto>("/stats");
        long baseline = baseStats!.NodeCount;

        for (int i = 0; i < 5; i++)
            await CreateNode("Counted");

        var after = await _client.GetFromJsonAsync<StatsDto>("/stats");
        after!.NodeCount.Should().Be(baseline + 5);
    }

    // ===== 8. concurrent GET (no exceptions) =====

    [Fact]
    public async Task Concurrent_GETs_for_mix_of_existing_and_ghost_IDs_never_throw_500()
    {
        var realIds = new List<long>();
        for (int i = 0; i < 8; i++)
            realIds.Add(await CreateNode("Concurrent"));

        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 32; i++)
        {
            long id = (i % 2 == 0) ? realIds[i % realIds.Count] : 1_000_000L + i;
            tasks.Add(_client.GetAsync($"/nodes/{id}"));
        }
        var responses = await Task.WhenAll(tasks);
        foreach (var r in responses)
        {
            ((int)r.StatusCode).Should().BeLessThan(500,
                "concurrent な ghost ID GET でも 500 が返らない (FT-30 中核契約)");
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
        json.Should().Contain("/nodes");
    }

    // ----- helpers -----

    private async Task<long> CreateNode(string label, string? name = null)
    {
        var resp = await _client.PostAsJsonAsync("/nodes", new { label, name });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<CreatedNodeDto>();
        return dto!.Id;
    }

    private sealed record CreatedNodeDto(long Id, string Label, string? Name);
    private sealed record GetNodeDto(long Id, string? Name);
    private sealed record StatsDto(long NodeCount, long RelationshipCount);
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
