using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Index.Tests;

/// <summary>
/// 1 つのインデックスファイルは単一型だけを格納する。同じ B+Tree に数値と文字列を混在させると
/// オンディスク形式が破損するため、<see cref="IndexManager"/> はプロセス内でも再起動後でも
/// 2 種類目の登録を拒否しなければならない。
/// </summary>
public sealed class IndexManagerTypeSafetyTests : IDisposable
{
    private readonly string _dir;

    public IndexManagerTypeSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_idxmeta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Index_records_type_flag_on_creation()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        _ = mgr.CreateInt64Index("by_age");

        mgr.GetIndexTypeFlags("by_age").Should().Be(PropertyTypeFlags.Int64);
    }

    [Fact]
    public void Reopening_same_index_with_same_type_returns_same_instance()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        var a = mgr.CreateInt64Index("by_age");
        var b = mgr.CreateInt64Index("by_age");
        b.Should().BeSameAs(a);
    }

    [Fact]
    public void Reopening_index_with_different_type_in_process_throws()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        _ = mgr.CreateInt64Index("by_age");

        Action act = () => mgr.CreateStringIndex("by_age");

        act.Should().Throw<ConstraintException>()
           .WithMessage("*by_age*Int64*String*");
    }

    [Fact]
    public void Reopening_index_with_different_type_after_restart_throws()
    {
        using (var mgr = IndexManager.OpenStandalone(_dir))
        {
            _ = mgr.CreateStringIndex("by_name");
        }

        using var reopened = IndexManager.OpenStandalone(_dir);

        Action act = () => reopened.CreateInt64Index("by_name");

        act.Should().Throw<ConstraintException>()
           .WithMessage("*by_name*String*Int64*");
    }

    [Fact]
    public void Reopening_index_with_same_type_after_restart_succeeds()
    {
        using (var mgr = IndexManager.OpenStandalone(_dir))
        {
            _ = mgr.CreateInt32Index("by_score");
        }

        using var reopened = IndexManager.OpenStandalone(_dir);
        _ = reopened.CreateInt32Index("by_score");
        reopened.GetIndexTypeFlags("by_score").Should().Be(PropertyTypeFlags.Int32);
    }

    [Fact]
    public void DropIndex_clears_persisted_type_flag()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        _ = mgr.CreateStringIndex("by_name");
        mgr.DropIndex("by_name").Should().BeTrue();
        mgr.GetIndexTypeFlags("by_name").Should().Be(PropertyTypeFlags.None);

        // 削除後は同名インデックスを別の型で作成できる。
        _ = mgr.CreateInt64Index("by_name");
        mgr.GetIndexTypeFlags("by_name").Should().Be(PropertyTypeFlags.Int64);
    }
}
