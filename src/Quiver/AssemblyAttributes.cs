using System.Runtime.CompilerServices;

// Phase 0 (ARCH-1): 統合により旧サブアセンブリの InternalsVisibleTo をここへ集約。
// テストアセンブリ + Benchmarks に公開する。
[assembly: InternalsVisibleTo("Quiver.Tests")]
[assembly: InternalsVisibleTo("Quiver.Backend.Tests")]
[assembly: InternalsVisibleTo("Quiver.Index.Tests")]
[assembly: InternalsVisibleTo("Quiver.Operators.Tests")]
[assembly: InternalsVisibleTo("Quiver.PropertyTests")]
[assembly: InternalsVisibleTo("Quiver.Storage.Tests")]
[assembly: InternalsVisibleTo("Quiver.Stores.Tests")]
[assembly: InternalsVisibleTo("Quiver.Transactions.Tests")]
[assembly: InternalsVisibleTo("Quiver.Wal.Tests")]
[assembly: InternalsVisibleTo("Quiver.Benchmarks")]
// ARCH-2: 内部実装 (codec / WAL parser / 物理オペレータ) を直接検査する dev/test 系。
[assembly: InternalsVisibleTo("Quiver.Codec.Tests")]
[assembly: InternalsVisibleTo("Quiver.FuzzTests")]
[assembly: InternalsVisibleTo("QuiverSandbox")]
