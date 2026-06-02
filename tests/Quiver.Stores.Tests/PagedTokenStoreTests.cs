using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// ARCH-4 増分3: トークンストアを単一ファイルコンテナのテナント (IPagedFile) 上で永続化できる
/// ことを検証する (PagedTokenPersistence)。従来の <c>*.tok</c> ファイルを単一ファイルへ吸収する。
/// </summary>
public class PagedTokenStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public PagedTokenStoreTests()
    {
        _tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => System.IO.Directory.Delete(_tmpDir, recursive: true);

    private string DbFile() => System.IO.Path.Combine(_tmpDir, "tokens.quiver");

    private const byte LabelTenant = 8; // 任意のテナント ID

    [Fact]
    public void Paged_tokens_persist_across_reopen()
    {
        string path = DbFile();
        LabelId person, movie;
        using (var c = new SingleFileContainer(path))
        {
            using var store = new LabelTokenStore(c.OpenTenant(LabelTenant, PageKind.TokenRecord));
            person = store.GetOrCreate("Person");
            movie = store.GetOrCreate("Movie");
            store.GetOrCreate("Person").Should().Be(person); // stable
            c.Flush();
        }

        using (var c = new SingleFileContainer(path))
        {
            using var store = new LabelTokenStore(c.OpenTenant(LabelTenant, PageKind.TokenRecord));
            store.TryGet("Person", out var p).Should().BeTrue();
            p.Should().Be(person);
            store.TryGet("Movie", out var m).Should().BeTrue();
            m.Should().Be(movie);
            store.GetName(person).Should().Be("Person");
            store.All().Should().HaveCount(2);
        }

        // 静止時は単一ファイルのみ (.tok サイドカーは無い)。
        System.IO.Directory.GetFiles(_tmpDir).Should().ContainSingle()
            .Which.Should().EndWith("tokens.quiver");
    }

    [Fact]
    public void Paged_tokens_handle_many_entries_spanning_pages()
    {
        string path = DbFile();
        // 各名前を長くして 1 ページ (8160B) を確実に跨ぐ。
        int n = 500;
        using (var c = new SingleFileContainer(path))
        {
            using var store = new PropertyKeyTokenStore(c.OpenTenant(9, PageKind.TokenRecord));
            for (int i = 0; i < n; i++)
                store.GetOrCreate($"property_key_with_a_reasonably_long_name_{i:D5}");
            c.Flush();
        }
        using (var c = new SingleFileContainer(path))
        {
            using var store = new PropertyKeyTokenStore(c.OpenTenant(9, PageKind.TokenRecord));
            store.All().Should().HaveCount(n);
            store.TryGet("property_key_with_a_reasonably_long_name_00000", out _).Should().BeTrue();
            store.TryGet($"property_key_with_a_reasonably_long_name_{(n - 1):D5}", out _).Should().BeTrue();
        }
    }

    [Fact]
    public void Paged_token_rename_persists()
    {
        string path = DbFile();
        LabelId id;
        using (var c = new SingleFileContainer(path))
        {
            using var store = new LabelTokenStore(c.OpenTenant(LabelTenant, PageKind.TokenRecord));
            id = store.GetOrCreate("OldName");
            store.Rename("OldName", "NewName").Should().BeTrue();
            c.Flush();
        }
        using (var c = new SingleFileContainer(path))
        {
            using var store = new LabelTokenStore(c.OpenTenant(LabelTenant, PageKind.TokenRecord));
            store.TryGet("NewName", out var nid).Should().BeTrue();
            nid.Should().Be(id); // id は維持
            store.TryGet("OldName", out _).Should().BeFalse();
        }
    }
}
